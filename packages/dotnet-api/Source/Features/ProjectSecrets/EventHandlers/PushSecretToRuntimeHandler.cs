using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Source.Features.Health.Services;
using Source.Features.ProjectSecrets.Events;
using Source.Features.ProjectSecrets.Models;
using Source.Features.ProjectSecrets.Services;
using Source.Features.RuntimeBootstrap.Contracts;
using Source.Features.RuntimeCuration.Services;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;
using Source.Infrastructure;
using Source.Shared;
using Source.Shared.Events;

namespace Source.Features.ProjectSecrets.EventHandlers;

/// <summary>
/// Reacts to <see cref="SecretsChanged"/> by pushing a typed env-var delta to
/// the project's running daemon over <see cref="RuntimeHub"/>. The daemon
/// rewrites <c>/data/.glenn/env</c> live; no runtime restart.
///
/// <para><b>Online-runtime predicate.</b> "Online" here means
/// <see cref="RuntimeState.Online"/> exclusively — that's the single state
/// where the daemon is connected to the hub and serving traffic
/// (<c>AgentHub.SubmitPrompt</c> uses the same gate). Bootstrapping / Waking
/// runtimes have a daemon process but it isn't ready to accept config
/// hot-applies yet; their env-var delivery happens on the connect-time
/// bootstrap path (spec project-secrets Card 5). Crashed / Failed /
/// Suspending / Suspended / Deleting / Deleted runtimes have no live daemon
/// at all.</para>
///
/// <para><b>Group convention.</b> Fan-out targets <c>runtime-{RuntimeId}</c>,
/// the group <see cref="RuntimeHub.OnConnectedAsync"/> joins each daemon to
/// on a verified handshake (see <c>RuntimeHub.cs:92</c>). One daemon per
/// runtime, one runtime per project, so the group has either zero or one
/// connection at any moment.</para>
///
/// <para><b>Plaintext lifetime.</b> When <see cref="SecretsChanged.Deleted"/>
/// is <c>false</c>, the handler decrypts the row and ships the plaintext on
/// the wire. The decrypt-then-send sequence pulls plaintext into a
/// <c>byte[]</c>, materialises a <c>string</c> for the payload, then
/// <see cref="CryptographicOperations.ZeroMemory"/>s the byte buffer
/// immediately. The string itself remains in the managed heap until GC —
/// .NET strings are immutable and we cannot deterministically zero them.
/// This is a known and accepted limitation; the encryption service
/// (<see cref="SecretEncryptionService"/>) makes the same trade-off in
/// <c>DecryptAsync</c>. Card 6 (logging redaction) ensures the plaintext
/// string never enters log output.</para>
///
/// <para><b>Failure mode.</b> Hub broadcast failures are swallowed — the
/// secret has already been persisted (the event would not have been raised
/// otherwise) and the bootstrap-env endpoint (Card 5) will reconcile the
/// daemon on its next reconnect. We log at Warning and move on.</para>
/// </summary>
public class PushSecretToRuntimeHandler : IEventHandler<SecretsChanged>
{
    private readonly IHubContext<RuntimeHub, IRuntimeClient> _hub;
    private readonly ApplicationDbContext _db;
    private readonly SecretEncryptionService _encryption;
    private readonly ICurrentExpandedSpecResolver _resolver;
    private readonly RestartServiceThrottle _throttle;
    private readonly IClock _clock;
    private readonly ILogger<PushSecretToRuntimeHandler> _logger;

    public PushSecretToRuntimeHandler(
        IHubContext<RuntimeHub, IRuntimeClient> hub,
        ApplicationDbContext db,
        SecretEncryptionService encryption,
        ICurrentExpandedSpecResolver resolver,
        RestartServiceThrottle throttle,
        IClock clock,
        ILogger<PushSecretToRuntimeHandler> logger)
    {
        _hub = hub;
        _db = db;
        _encryption = encryption;
        _resolver = resolver;
        _throttle = throttle;
        _clock = clock;
        _logger = logger;
    }

    public async Task Handle(SecretsChanged notification, CancellationToken cancellationToken)
    {
        // Most-recent non-soft-deleted runtime for the project. The global
        // query filter on ProjectRuntime hides IsDeleted=true rows, and the
        // OrderByDescending(CreatedAt) tiebreak mirrors RuntimeStatusController.
        // In normal operation there's only ever one row per project; the
        // tiebreak is a safety net for an edge case where a teardown and a
        // reprovision overlap and both rows briefly coexist.
        var runtime = await _db.ProjectRuntimes
            .AsNoTracking()
            .Where(r => r.ProjectId == notification.ProjectId)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (runtime is null || runtime.State != RuntimeState.Online)
        {
            _logger.LogInformation(
                "PushSecretToRuntime: no online runtime for project {ProjectId}; secret will land on next bootstrap (key {Key}, deleted={Deleted}, branch={BranchId}).",
                notification.ProjectId,
                notification.ChangedKey,
                notification.Deleted,
                notification.BranchId);
            return;
        }

        // Branch-effective gating. A change is only relevant to THIS runtime if
        // it changes the value the runtime's branch actually sees.
        //
        //  - Branch-specific change (event.BranchId != null): only relevant when
        //    it targets the runtime's own branch. A change on some OTHER branch
        //    never touches this runtime.
        //  - Project-wide change (event.BranchId == null): relevant only if the
        //    runtime's branch does NOT have its own override for the key. If a
        //    branch override exists, the runtime is already pinned to the branch
        //    value, so a project-wide add/update/delete must NOT clobber it.
        if (notification.BranchId is not null && notification.BranchId != runtime.BranchId)
        {
            _logger.LogInformation(
                "PushSecretToRuntime: change on branch {ChangedBranchId} is not effective for runtime {RuntimeId} on branch {RuntimeBranchId}; skipping (key {Key}).",
                notification.BranchId,
                runtime.Id,
                runtime.BranchId,
                notification.ChangedKey);
            return;
        }

        if (notification.BranchId is null)
        {
            // Project-wide change: does the runtime's branch override this key?
            var branchOverride = await _db.ProjectSecrets
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    s => s.ProjectId == notification.ProjectId
                      && s.BranchId == runtime.BranchId
                      && s.Key == notification.ChangedKey,
                    cancellationToken);

            if (branchOverride is not null)
            {
                // Branch value wins — the runtime already sees the override, so
                // the project-wide change is a no-op for this runtime.
                _logger.LogInformation(
                    "PushSecretToRuntime: project-wide change for key {Key} is shadowed by a branch override on runtime {RuntimeId} branch {BranchId}; skipping.",
                    notification.ChangedKey,
                    runtime.Id,
                    runtime.BranchId);
                return;
            }
        }

        EnvVarDelta delta;
        if (notification.Deleted)
        {
            // Delete handling has two sub-cases for what the runtime should end
            // up with:
            //
            //  - A branch-specific delete (event.BranchId == runtime.BranchId)
            //    means the override is gone; the runtime should fall BACK to the
            //    project-wide default for that key if one exists, rather than
            //    losing the var entirely. So we look for a surviving project-wide
            //    row and, if found, push ITS decrypted value instead of a removal.
            //  - A project-wide delete (event.BranchId == null) reaches here only
            //    when there is NO branch override (the shadow check above would
            //    have skipped otherwise), so removing the line is correct.
            //
            // If no fallback row exists, null Value tells the daemon to remove
            // the line.
            ProjectSecret? fallback = null;
            if (notification.BranchId is not null)
            {
                fallback = await _db.ProjectSecrets
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        s => s.ProjectId == notification.ProjectId
                          && s.BranchId == null
                          && s.Key == notification.ChangedKey,
                        cancellationToken);
            }

            if (fallback is null)
            {
                delta = new EnvVarDelta(notification.ChangedKey, null);
            }
            else
            {
                var plaintext = await _encryption.DecryptAsync(
                    notification.ProjectId,
                    fallback.Ciphertext,
                    fallback.Nonce,
                    fallback.DekVersion,
                    cancellationToken);

                byte[]? fallbackBytes = null;
                try
                {
                    fallbackBytes = Encoding.UTF8.GetBytes(plaintext);
                    var payloadString = Encoding.UTF8.GetString(fallbackBytes);
                    delta = new EnvVarDelta(notification.ChangedKey, payloadString);
                }
                finally
                {
                    if (fallbackBytes is not null)
                    {
                        CryptographicOperations.ZeroMemory(fallbackBytes);
                    }
                }
            }
        }
        else
        {
            // Add or Update: re-find the exact changed row by (ProjectId,
            // BranchId, Key) and decrypt. The global query filter already
            // excludes soft-deleted rows so a race between this handler and a
            // delete cleanly yields a missing row, which we treat as "already
            // gone" and bail.
            var secret = await _db.ProjectSecrets
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    s => s.ProjectId == notification.ProjectId
                      && s.BranchId == notification.BranchId
                      && s.Key == notification.ChangedKey,
                    cancellationToken);

            if (secret is null)
            {
                _logger.LogInformation(
                    "PushSecretToRuntime: secret row for project {ProjectId} key {Key} branch {BranchId} no longer exists; nothing to push.",
                    notification.ProjectId,
                    notification.ChangedKey,
                    notification.BranchId);
                return;
            }

            // Decrypt path. The encryption service hands back a string already;
            // we re-encode through a byte[] solely so we have something we can
            // ZeroMemory after the payload is built. The string the payload
            // carries is unavoidably resident on the heap until GC.
            var plaintext = await _encryption.DecryptAsync(
                notification.ProjectId,
                secret.Ciphertext,
                secret.Nonce,
                secret.DekVersion,
                cancellationToken);

            byte[]? plaintextBytes = null;
            try
            {
                plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
                var payloadString = Encoding.UTF8.GetString(plaintextBytes);
                delta = new EnvVarDelta(notification.ChangedKey, payloadString);
            }
            finally
            {
                if (plaintextBytes is not null)
                {
                    CryptographicOperations.ZeroMemory(plaintextBytes);
                }
            }
        }

        var payload = new ConfigUpdatePayload(
            RuntimeId: runtime.Id,
            Version: "1",
            RuntimeToken: null,
            HooksJson: null,
            AutoCommit: null,
            DeployKey: null,
            EnvVarsDelta: new List<EnvVarDelta> { delta });

        try
        {
            await _hub.Clients
                .Group($"runtime-{runtime.Id}")
                .UpdateConfig(payload);

            _logger.LogInformation(
                "PushSecretToRuntime: pushed env-var delta to runtime {RuntimeId} for project {ProjectId} (key {Key}, deleted={Deleted}).",
                runtime.Id,
                notification.ProjectId,
                notification.ChangedKey,
                notification.Deleted);
        }
        catch (Exception ex)
        {
            // Persistence is unaffected. Card 5's bootstrap-env endpoint
            // delivers on the next daemon reconnect — clients will catch up.
            _logger.LogWarning(ex,
                "PushSecretToRuntime: UpdateConfig push failed for runtime {RuntimeId} project {ProjectId} key {Key}; daemon will reconcile on next reconnect.",
                runtime.Id,
                notification.ProjectId,
                notification.ChangedKey);
        }

        // Auto-restart-on-fill. When a fill/update lands a value for an env var
        // that a service declared as REQUIRED, the hot env-delta above rewrites
        // /data/.glenn/env but already-running services don't re-read their
        // environment — they need a restart to pick up the new value. We resolve
        // the current expanded V2 spec, find every service whose RequiredEnv
        // contains the changed key, and push a throttled RestartService for each.
        //
        // Best-effort only: a delete never triggers a restart (clearing a value
        // shouldn't bounce a service), and any failure here is swallowed — the
        // secret write has already succeeded and must not be undone by a restart
        // push that fails. Mirrors the UpdateConfig resilience above.
        if (!notification.Deleted)
        {
            try
            {
                var specJson = await _resolver.ResolveAsync(runtime.ProjectId, null, cancellationToken);
                if (!string.IsNullOrWhiteSpace(specJson))
                {
                    var parsed = RuntimeSpecV2.TryParse(specJson);
                    if (parsed.IsSuccess && parsed.Value!.Services is { Count: > 0 } services)
                    {
                        foreach (var service in services)
                        {
                            var declaresKey = service.RequiredEnv?
                                .Any(r => string.Equals(r.Key, notification.ChangedKey, StringComparison.Ordinal)) == true;
                            if (!declaresKey)
                            {
                                continue;
                            }

                            if (!_throttle.TryClaim(runtime.Id, service.Name, _clock.UtcNow))
                            {
                                _logger.LogDebug(
                                    "PushSecretToRuntime: restart-on-fill for runtime {RuntimeId} service {ServiceName} (key {Key}) throttled; skipping.",
                                    runtime.Id,
                                    service.Name,
                                    notification.ChangedKey);
                                continue;
                            }

                            await _hub.Clients
                                .Group($"runtime-{runtime.Id}")
                                .RestartService(new RestartServicePayload(
                                    RuntimeId: runtime.Id,
                                    ServiceName: service.Name,
                                    Reason: $"required env var {notification.ChangedKey} provided",
                                    RequestId: Guid.NewGuid()));

                            _logger.LogInformation(
                                "PushSecretToRuntime: dispatched restart-on-fill for runtime {RuntimeId} service {ServiceName} after required env var {Key} was provided.",
                                runtime.Id,
                                service.Name,
                                notification.ChangedKey);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Secret persistence + env-delta push are unaffected. A failed
                // restart push is non-fatal — the daemon's heartbeat-respawn /
                // next bootstrap reconciles, and the operator can restart manually.
                _logger.LogWarning(ex,
                    "PushSecretToRuntime: restart-on-fill dispatch failed for runtime {RuntimeId} project {ProjectId} key {Key}; service(s) will pick up the value on next restart.",
                    runtime.Id,
                    notification.ProjectId,
                    notification.ChangedKey);
            }
        }
    }
}

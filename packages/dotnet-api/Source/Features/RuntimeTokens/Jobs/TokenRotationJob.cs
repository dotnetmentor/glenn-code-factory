using Hangfire;
using MediatR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.RuntimeTokens.Commands;
using Source.Features.RuntimeTokens.Services;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;
using Source.Infrastructure;

namespace Source.Features.RuntimeTokens.Jobs;

/// <summary>
/// Daily Hangfire job. For every runtime whose currently active token is
/// approaching expiry, mint a fresh one and push it to the daemon via
/// <see cref="IRuntimeClient.UpdateConfig"/>. The previous token stays valid
/// for a 1-hour overlap window and is then revoked by a scheduled
/// <see cref="RevokeTokenCommand"/>.
///
/// <para><b>Why a 1-day lookahead and a 1-hour overlap.</b> Tokens have a
/// 7-day default lifetime per the spec. Rotating once a day with a 1-day
/// look-ahead means a missed run still has 6 days of slack before any
/// runtime would actually need a fresh token. The 1-hour overlap is long
/// enough to absorb a daemon that's offline at rotation time and reconnects
/// minutes later (or fails over via respawn) but short enough that a leaked
/// old token has bounded blast radius.</para>
///
/// <para><b>Why one mint per runtime, not per stale token.</b> A runtime
/// may have multiple non-revoked, non-expired token rows if previous
/// rotations went weird. We rotate the most recent one (highest IssuedAt)
/// and revoke any other still-alive ones for the same runtime in the same
/// pass — the revocation cleans up drift without needing a separate sweeper.
/// </para>
///
/// <para><b>Daemon-side handler is out of scope here.</b> This job only
/// publishes <see cref="ConfigUpdatePayload"/> over SignalR; consuming the
/// new token (overwriting the in-memory copy + writing through to
/// <c>/data/.glenn/env</c>) lives in spec <c>daemon-architecture</c>. The
/// 1-hour overlap covers the gap between push and consume, including
/// daemons that are offline at rotation time.</para>
/// </summary>
public class TokenRotationJob
{
    private readonly ApplicationDbContext _db;
    private readonly IRuntimeTokenService _runtimeTokenService;
    private readonly IHubContext<RuntimeHub, IRuntimeClient> _runtimeHub;
    private readonly IBackgroundJobClient _backgroundJobs;
    private readonly IMediator _mediator;
    private readonly ILogger<TokenRotationJob> _logger;

    /// <summary>How close to expiry a token has to be before we rotate.</summary>
    public static readonly TimeSpan RotationLookahead = TimeSpan.FromDays(1);

    /// <summary>How long the old token stays valid after the new one is issued.</summary>
    public static readonly TimeSpan OverlapWindow = TimeSpan.FromHours(1);

    public TokenRotationJob(
        ApplicationDbContext db,
        IRuntimeTokenService runtimeTokenService,
        IHubContext<RuntimeHub, IRuntimeClient> runtimeHub,
        IBackgroundJobClient backgroundJobs,
        IMediator mediator,
        ILogger<TokenRotationJob> logger)
    {
        _db = db;
        _runtimeTokenService = runtimeTokenService;
        _runtimeHub = runtimeHub;
        _backgroundJobs = backgroundJobs;
        _mediator = mediator;
        _logger = logger;
    }

    /// <summary>
    /// Hangfire entry point. Wraps the inner <see cref="Run(CancellationToken)"/>
    /// in a linked <see cref="CancellationTokenSource"/> with a hard 280-second
    /// budget so the job can never hold the
    /// <see cref="DisableConcurrentExecutionAttribute"/> lock past the 300-second
    /// TTL — even if a SignalR push, EF call, or mint hangs forever. When the
    /// CTS trips, control returns, Hangfire releases the lock, and the next
    /// daily tick acquires cleanly.
    ///
    /// <para><see cref="AutomaticRetry"/> with <c>Attempts = 0</c> stops Hangfire
    /// from auto-requeuing a partially-cancelled run on top of the next scheduled
    /// tick.</para>
    /// </summary>
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    [AutomaticRetry(Attempts = 0)]
    public async Task Run(IJobCancellationToken hangfireCt)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(hangfireCt.ShutdownToken);
        cts.CancelAfter(TimeSpan.FromSeconds(280));
        await Run(cts.Token);
    }

    public async Task Run(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var rotateBy = now + RotationLookahead;

        // Token rows nearing expiry, joined to the live runtime so we can
        // skip Failed/Deleted runtimes (no point pushing tokens to a dead
        // daemon — janitor will tear them down).
        // The join intentionally bypasses the soft-delete filter on
        // ProjectRuntime by using IgnoreQueryFilters() so a Deleted runtime
        // is treated as "filter out", not "row gone, skip silently". The
        // State + IsDeleted checks are the gate that matters.
        var candidates = await (
            from t in _db.RuntimeTokenIssues
            join r in _db.ProjectRuntimes.IgnoreQueryFilters()
                on t.RuntimeId equals r.Id
            where t.RevokedAt == null
                && t.ExpiresAt < rotateBy
                && t.ExpiresAt > now            // skip already-expired (revoke-via-natural-lifetime; nothing to push to)
                && r.State != RuntimeState.Failed
                && r.State != RuntimeState.Deleted
                && r.State != RuntimeState.Deleting
                && !r.IsDeleted
            select new { Token = t, Runtime = r }
        ).ToListAsync(ct);

        if (candidates.Count == 0)
        {
            _logger.LogInformation("TokenRotationJob: no rotation candidates.");
            return;
        }

        // Group by runtime. Mint exactly one new token per runtime.
        // For runtimes that somehow have multiple alive token rows, we
        // rotate against the most-recent and the others ride the overlap
        // -> revoke path same as the "old" token does.
        var grouped = candidates.GroupBy(c => c.Runtime.Id).ToList();

        _logger.LogInformation("TokenRotationJob: {Count} runtimes due for rotation.", grouped.Count);

        foreach (var group in grouped)
        {
            ct.ThrowIfCancellationRequested();
            var runtime = group.First().Runtime;
            var oldTokens = group.Select(g => g.Token).OrderByDescending(t => t.IssuedAt).ToList();
            var primaryOld = oldTokens.First();

            try
            {
                var mintResult = await _runtimeTokenService.MintAsync(new MintTokenRequest(
                    RuntimeId: runtime.Id,
                    ProjectId: runtime.ProjectId,
                    BranchId: null,
                    TenantId: runtime.TenantId,
                    Scope: primaryOld.Scope
                ), ct);

                if (mintResult.IsFailure)
                {
                    // Most likely: the runtime row is missing TenantId, which
                    // the token service refuses to mint for. Skip rotation for
                    // this runtime — leaving the old token alive until either
                    // the row is fixed or it expires naturally — and let the
                    // next tick retry.
                    _logger.LogError(
                        "TokenRotationJob: token mint rejected for runtime {RuntimeId}: {Error}",
                        runtime.Id, mintResult.Error);
                    continue;
                }

                var mint = mintResult.Value;

                // Push to the daemon group for this runtime. UpdateConfig is
                // best-effort: a daemon that's offline misses the push and the
                // overlap window covers the gap until reconnect or respawn.
                // We swallow per-runtime push errors so one bad daemon doesn't
                // block the rest of the batch.
                try
                {
                    await _runtimeHub.Clients
                        .Group($"runtime-{runtime.Id}")
                        .UpdateConfig(new ConfigUpdatePayload(
                            RuntimeId: runtime.Id,
                            Version: "1",
                            RuntimeToken: mint.Token));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "TokenRotationJob: UpdateConfig push failed for runtime {RuntimeId}; new token already minted, daemon will pick it up on next reconnect.",
                        runtime.Id);
                }

                // Schedule the old token(s) for revocation after the overlap
                // window. Use IBackgroundJobClient.Schedule so Hangfire owns
                // the delay; if main API restarts before the hour is up, the
                // job survives.
                foreach (var oldToken in oldTokens)
                {
                    _backgroundJobs.Schedule<TokenRotationJob>(
                        j => j.RevokeRotated(oldToken.Id, "rotation"),
                        OverlapWindow);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "TokenRotationJob: rotation failed for runtime {RuntimeId}; will retry on next tick.",
                    runtime.Id);
            }
        }
    }

    /// <summary>
    /// Hangfire-invocable wrapper around <see cref="RevokeTokenCommand"/>. Lives
    /// on the job class itself so the scheduled-job expression stays a single
    /// strongly-typed call. Delegates to <see cref="IMediator"/> so the
    /// command's full policy (cache prime, idempotency, already-expired-safe,
    /// reason validation) flows through one path — the job is just the trigger.
    /// </summary>
    public async Task RevokeRotated(Guid jti, string reason)
    {
        // Idempotent + already-expired-safe per Card 5 of runtime-tokens.
        // Result.Failure("token_not_found") is fine here — the job is best-effort
        // and the row may have been hard-deleted by the time we run.
        await _mediator.Send(new RevokeTokenCommand(jti, reason));
    }
}

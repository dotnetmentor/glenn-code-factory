using Microsoft.EntityFrameworkCore;
using Source.Features.BoxManagement;
using Source.Features.BoxManagement.Models;
using Source.Features.Cloudflare.Models;
using Source.Features.Cloudflare.Services;
using Source.Features.DaemonVersions.Models;
using Source.Features.Projects.Models;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.SystemSettings.Services;
using Source.Infrastructure;

namespace Source.Features.RuntimeLifecycle.Provisioning;

/// <summary>
/// Shared Box provisioning helpers: box naming, idempotent fork, the
/// env-refresh side channel, the wait-until-up poll loop, and operator-facing
/// error copy. The Box-native successor to the old <c>RuntimeFlyProvisioning</c>.
/// </summary>
public static class BoxRuntimeProvisioning
{
    /// <summary>Deterministic box name for a runtime — lets a crashed provisioner adopt its own half-created fork.</summary>
    public static string BuildBoxName(Guid runtimeId) =>
        $"rt-{runtimeId:N}"[..30];

    /// <summary>
    /// Absolute path of the env file the daemon's systemd unit reads
    /// (<c>EnvironmentFile=</c> in the template's <c>glenn-daemon.service</c>).
    /// The fork-time per-fork env is the primary delivery channel; this file is
    /// the refresh channel for env that changes after the fork (fresh runtime
    /// JWT on respawn, rotated tunnel token, ...).
    /// </summary>
    public const string RuntimeEnvFilePath = "/etc/glenn/runtime.env";

    /// <summary>Systemd unit name the template installs for the daemon bootstrap.</summary>
    public const string DaemonServiceName = "glenn-daemon";

    /// <summary>
    /// How long <see cref="WaitForBoxUpAsync"/> polls before giving up. Resumes
    /// complete "in a few seconds"; fresh forks take somewhat longer. 40s keeps us
    /// inside the provisioner's 50s job budget.
    /// </summary>
    public static readonly TimeSpan DefaultUpTimeout = TimeSpan.FromSeconds(40);

    /// <summary>
    /// Human-readable failure text for UI surfaces (<c>RuntimeStatusResponse.ErrorMessage</c>).
    /// Raw Box bodies stay in logs and RuntimeStateEvent audit rows.
    /// </summary>
    public static string FormatUserMessage(BoxApiException ex)
    {
        var code = ex.ErrorCode ?? string.Empty;

        if (code.Contains("daily_limit", StringComparison.OrdinalIgnoreCase)
            || code.Contains("rate_limited", StringComparison.OrdinalIgnoreCase)
            || code.Contains("start_limit_reached", StringComparison.OrdinalIgnoreCase))
        {
            return
                "Box's machine-start budget is exhausted for now (starts are capped per hour and per day account-wide). "
                + "The runtime will retry automatically; if this recurs, contact Box to raise the account limits.";
        }

        if (!string.IsNullOrWhiteSpace(code))
        {
            return $"Box rejected the runtime request ({code}). Check Super Admin → Runtime Monitor for details.";
        }

        return "Box rejected the runtime request. Check Super Admin → Runtime Monitor for details.";
    }

    /// <summary>
    /// True for failures that should leave the runtime Pending for the next sweep
    /// instead of marking it Failed: start-budget exhaustion, rate limiting
    /// (<c>rate_limited</c> / <c>start_limit_reached</c> on 429), and
    /// the box-still-starting 409s.
    /// </summary>
    public static bool IsTransient(BoxApiException ex) =>
        ex.IsRetriableStartup
        || ex.IsRateLimited
        || (ex.ErrorCode?.Contains("limit", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>
    /// Fork the template into a runtime box, or adopt an existing box already
    /// carrying this runtime's deterministic name — the leftovers of a prior
    /// attempt that forked + named the box but crashed before persisting state.
    ///
    /// <para>The fork body has no <c>name</c> field (per the contract), so
    /// adoption can't ride a name-conflict 409 any more: instead we list + match
    /// on <c>box.name</c> BEFORE forking, and PATCH the deterministic name onto
    /// the fresh fork right after it's created so the next crashed attempt can
    /// adopt it the same way.</para>
    /// </summary>
    public static async Task<BoxVm> ForkOrAdoptAsync(
        BoxClient box,
        ApplicationDbContext db,
        ProjectRuntime runtime,
        string templateBoxId,
        ForkBoxRequest request,
        string boxName,
        ILogger logger,
        CancellationToken ct)
    {
        var adopted = await TryAdoptBoxByNameAsync(box, db, runtime, boxName, ct);
        if (adopted is not null)
        {
            logger.LogInformation(
                "BoxRuntimeProvisioning: adopted existing box {BoxId} named {BoxName} for runtime {RuntimeId} (crashed prior attempt)",
                adopted.Id, boxName, runtime.Id);
            return adopted;
        }

        var idempotencyKey = $"fork-box:{runtime.Id:D}";
        var forked = await box.ForkBoxAsync(
            templateBoxId,
            request,
            idempotencyKey: idempotencyKey,
            runtimeId: runtime.Id,
            ct: ct);

        // Stamp the deterministic name so a crashed provisioner can re-adopt this
        // fork on its next attempt. Best-effort: an unnamed box is still fully
        // functional and the TTL guardrail catches true orphans.
        try
        {
            await box.SetNameAsync(forked.Id, boxName, runtime.Id, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "BoxRuntimeProvisioning: could not PATCH name {BoxName} onto box {BoxId} (runtime {RuntimeId}); adopt-by-name won't find it if this attempt crashes.",
                boxName, forked.Id, runtime.Id);
        }

        return forked;
    }

    /// <summary>
    /// Look for an existing box carrying <paramref name="boxName"/> that no OTHER
    /// runtime row owns — the leftovers of a crashed prior provisioning attempt for
    /// THIS runtime. Returns null when there's nothing safely adoptable.
    /// </summary>
    public static async Task<BoxVm?> TryAdoptBoxByNameAsync(
        BoxClient box,
        ApplicationDbContext db,
        ProjectRuntime runtime,
        string? boxName,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(boxName))
        {
            return null;
        }

        var boxes = await box.ListBoxesAsync(ct);
        var match = boxes.FirstOrDefault(b =>
            string.Equals(b.Name, boxName, StringComparison.Ordinal));

        if (match is null)
        {
            return null;
        }

        var ownedByOther = await db.ProjectRuntimes
            .AnyAsync(r => r.BoxId == match.Id && r.Id != runtime.Id, ct);

        return ownedByOther ? null : match;
    }

    /// <summary>
    /// Poll until the box reports an up state (<c>ready</c>/<c>idle</c>/<c>running</c>)
    /// or the timeout lapses. Box has no server-side long-poll wait endpoint, so this
    /// is a plain 2-second poll loop. Returns the last observed box (up or not) so the
    /// caller can decide how hard to fail; returns null only if even GetBox kept failing.
    /// </summary>
    public static async Task<BoxVm?> WaitForBoxUpAsync(
        BoxClient box,
        string boxId,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        BoxVm? last = null;

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                last = await box.GetBoxAsync(boxId, ct);
                if (BoxStates.IsUp(last.State) || BoxStates.IsError(last.State))
                {
                    return last;
                }
            }
            catch (BoxApiException ex) when (ex.IsRetriableStartup)
            {
                // Still provisioning/resuming — keep polling.
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        return last;
    }

    /// <summary>
    /// Refresh the runtime env file inside a RUNNING box and bounce the daemon so it
    /// picks the new values up. This is the post-fork env channel: per-fork env is
    /// immutable after creation, but respawns mint a fresh runtime JWT and tunnel
    /// tokens can rotate. One command round-trip: write the file, then
    /// <c>systemctl restart glenn-daemon</c>.
    ///
    /// <para>The heredoc uses a quoted delimiter so values pass byte-for-byte with no
    /// shell expansion. Values are additionally single-quote-escaped since env values
    /// (JWTs, URLs) must never terminate the quoting early.</para>
    /// </summary>
    public static async Task RefreshEnvAndRestartDaemonAsync(
        BoxClient box,
        string boxId,
        IReadOnlyDictionary<string, string> env,
        Guid runtimeId,
        CancellationToken ct)
    {
        var lines = env.Select(kv => $"{kv.Key}='{kv.Value.Replace("'", "'\\''")}'");
        var fileBody = string.Join("\n", lines);

        // Box's commands endpoint executes as the unprivileged `user` account
        // (passwordless sudo) — /etc/glenn and systemctl need root, hence sudo
        // on every privileged step.
        var command =
            $"sudo mkdir -p /etc/glenn && sudo tee {RuntimeEnvFilePath} >/dev/null <<'GLENN_ENV_EOF'\n"
            + fileBody
            + "\nGLENN_ENV_EOF\n"
            + $"sudo chown agent:agent {RuntimeEnvFilePath} && sudo chmod 600 {RuntimeEnvFilePath}"
            + $" && sudo systemctl restart {DaemonServiceName}";

        // Explicit timeout: the contract default is 30s; a systemctl restart on a
        // just-resumed box can exceed that, so give the refresh a 120s budget.
        var result = await box.RunCommandAsync(boxId, command, runtimeId, timeoutSeconds: 120, ct: ct);

        // The commands endpoint reports shell failure via exitCode on a 200 —
        // surface it, otherwise a failed refresh looks like success and the
        // daemon silently boots with stale env (expired JWT → crash loop).
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Env refresh command on box {boxId} exited with {result.ExitCode?.ToString() ?? "null"}"
                + $" (timedOut={result.TimedOut}): {Truncate(result.Stderr, 500)}");
        }
    }

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty
        : value.Length <= max ? value
        : value[..max];

    /// <summary>
    /// Build the env-var contract the daemon boots with — shared by the provisioner
    /// (fresh forks + reboots) and the respawn job so the two can never diverge:
    /// <c>RUNTIME_ID</c> + JWT + <c>MAIN_API_URL</c>, informational daemon-bundle
    /// stamps (the bootstrap script re-resolves them at boot for hot-reload
    /// semantics), and the Cloudflare preview-tunnel trio when the branch has an
    /// assigned subdomain. Also performs the defensive Cloudflare ingress
    /// reconciliation (idempotent PUT, best-effort) for non-default preview ports.
    /// </summary>
    public static async Task<Dictionary<string, string>> BuildRuntimeEnvAsync(
        ApplicationDbContext db,
        ISystemSettingsCipher cipher,
        CloudflareApiClient cloudflare,
        string publicApiUrl,
        ProjectRuntime runtime,
        DaemonVersionDto daemon,
        string runtimeToken,
        ILogger logger,
        CancellationToken ct)
    {
        var env = new Dictionary<string, string>
        {
            ["RUNTIME_ID"] = runtime.Id.ToString(),
            ["GLENN_RUNTIME_TOKEN"] = runtimeToken,
            // MAIN_API_URL is BOTH the daemon's callback URL AND the URL the
            // bootstrap script uses to RESOLVE the daemon bundle at boot.
            ["MAIN_API_URL"] = publicApiUrl,
            ["DAEMON_VERSION"] = daemon.Version,
            ["DAEMON_BUNDLE_URL"] = daemon.DownloadUrl,
            ["DAEMON_BUNDLE_SHA256"] = daemon.Sha256,
        };

        var subdomain = await db.SubdomainAssignments
            .Where(s => s.AssignedBranchId == runtime.BranchId
                        && s.Status == SubdomainStatus.Assigned)
            .FirstOrDefaultAsync(ct);

        var previewPort = await db.Projects
            .Where(p => p.Id == runtime.ProjectId)
            .Select(p => (int?)p.PreviewPort)
            .FirstOrDefaultAsync(ct) ?? Project.DefaultPreviewPort;

        if (subdomain is not null)
        {
            env["TUNNEL_TOKEN"] = cipher.Decrypt(subdomain.TunnelToken);
            env["PREVIEW_PORT"] = previewPort.ToString();
            env["PREVIEW_HOSTNAME"] = subdomain.Hostname;

            if (previewPort != Project.DefaultPreviewPort)
            {
                try
                {
                    await cloudflare.AddPublicHostnameAsync(
                        subdomain.TunnelId,
                        subdomain.Hostname,
                        previewPort,
                        ct);
                    logger.LogInformation(
                        "BoxRuntimeProvisioning: reconciled tunnel {TunnelId} ingress to localhost:{PreviewPort} for runtime {RuntimeId}",
                        subdomain.TunnelId, previewPort, runtime.Id);
                }
                catch (Exception ex)
                {
                    // Best-effort — a Cloudflare blip must not block the boot; the
                    // tunnel may briefly route to the placeholder port until the
                    // next reconciliation catches up.
                    logger.LogWarning(
                        ex,
                        "BoxRuntimeProvisioning: Cloudflare ingress PUT failed for tunnel {TunnelId} (runtime {RuntimeId}, port {PreviewPort}). Proceeding with boot.",
                        subdomain.TunnelId, runtime.Id, previewPort);
                }
            }
        }
        else
        {
            logger.LogInformation(
                "BoxRuntimeProvisioning: runtime {RuntimeId} (branch {BranchId}) has no assigned subdomain — skipping preview-tunnel env vars (legacy branch).",
                runtime.Id, runtime.BranchId);
        }

        return env;
    }
}

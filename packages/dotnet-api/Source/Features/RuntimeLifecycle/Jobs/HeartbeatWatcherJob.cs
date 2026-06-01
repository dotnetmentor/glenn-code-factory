using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;
using Source.Shared;

namespace Source.Features.RuntimeLifecycle.Jobs;

/// <summary>
/// Recurring Hangfire job that detects runtimes whose daemon has gone silent
/// and transitions them to <see cref="RuntimeState.Crashed"/>. Runs every minute
/// via <see cref="HeartbeatWatcherJobRegistration"/>; the job itself fans out
/// internally with a 12 x 5-second loop so the effective scan cadence is 5 s
/// without paying the per-second Hangfire registration tax.
///
/// <para><b>Threshold.</b> 60 seconds — twelve missed 5-second heartbeats plus
/// a generous buffer for clock skew, request latency, backend restarts, and
/// transient SignalR reconnects. The previous 15s threshold false-positived
/// during long heavy turns on small Fly machines (1 shared CPU / 2 GB), 30s
/// false-positived during routine backend restarts (the watcher woke against
/// stale <c>LastHeartbeatAt</c> rows and burned the 3-crash respawn budget for
/// every active runtime in under a minute). The daemon-side fixes (heartbeat
/// in a worker_thread, self-watchdog at 50s, SDK→SignalR backpressure) handle
/// the real failure modes; this threshold just stops punishing transient
/// starvation. Anything more conservative leaves an actually-crashed daemon
/// undetected for too long; 60s is roughly the upper bound of "still
/// acceptable detection latency" for the live-chat UX.</para>
///
/// <para><b>Scope.</b> Only states that <i>expect</i> heartbeats are scanned:
/// <see cref="RuntimeState.Online"/>, <see cref="RuntimeState.Bootstrapping"/>
/// and <see cref="RuntimeState.Waking"/>. <see cref="RuntimeState.Booting"/> is
/// excluded — the daemon hasn't connected yet — as are
/// <see cref="RuntimeState.Suspended"/> / <see cref="RuntimeState.Suspending"/>
/// (the daemon is intentionally quiet) and the terminal/operator states. The
/// <c>LastHeartbeatAt != null</c> filter skips runtimes that just transitioned
/// in and have no heartbeat yet.</para>
///
/// <para><b>Concurrency.</b> Decorated with
/// <see cref="DisableConcurrentExecutionAttribute"/> with a 60-second timeout
/// (the loop sleeps 11 x 5 s = 55 s + scan time) so two Hangfire workers can't
/// overlap on the same minute.</para>
///
/// <para><b>Failure isolation.</b> Per-iteration <c>try/catch</c> ensures one
/// bad scan iteration doesn't kill the remaining 11; per-row transition
/// failures are logged at warning level and skipped. A single
/// <c>SaveChangesAsync</c> per iteration batches the domain events.</para>
/// </summary>
public class HeartbeatWatcherJob
{
    /// <summary>
    /// Silence threshold in seconds. 12 missed 5-second beats + a small buffer.
    ///
    /// <para>Raised from 30s → 60s after a backend-restart cascade: a routine
    /// "restart the API to pick up a .cs change" caused the watcher to wake
    /// against stale <c>LastHeartbeatAt</c> timestamps, flagged every active
    /// runtime as Crashed, and burned the 3-crash respawn budget within a
    /// minute. 60s comfortably survives a normal backend restart (typically
    /// 10–20s) plus a SignalR reconnect window plus a transient network blip.</para>
    ///
    /// <para><b>Invariant with daemon self-watchdog:</b> the daemon's
    /// <c>SelfWatchdog</c> SIGKILLs itself at ~50s of silence — i.e. ~10s
    /// before this watcher would flag the row Crashed. That gap lets the
    /// daemon self-respawn (entrypoint.sh loop) instead of getting
    /// Fly-destroyed by the master, which is faster and cheaper. If you
    /// change this value, change <c>DEFAULT_THRESHOLD_MS</c> in
    /// <c>packages/daemon/src/heartbeat/SelfWatchdog.ts</c> in lockstep.</para>
    /// </summary>
    private const int ThresholdSeconds = 60;

    /// <summary>
    /// Bootstrap <b>silence</b> threshold in minutes for runtimes stuck in mid-boot
    /// states (Booting / Bootstrapping / Waking) that have NEVER produced a
    /// heartbeat. This measures <i>silence</i> — the gap since the last sign of
    /// bootstrap activity — NOT total time spent in the state.
    ///
    /// <para><b>Why silence, not time-in-state (the bug this fixes).</b> Bootstrap
    /// progress (clone, <c>dotnet restore</c>, build, <c>npm install</c>, service
    /// start) streams exclusively to <c>RuntimeEvents</c> via
    /// <c>RecordRuntimeEventCommandHandler</c>; none of it touches the
    /// <see cref="ProjectRuntime"/> row, so <c>UpdatedAt</c> stays frozen at the
    /// time of the last state transition. The previous version of this branch
    /// used that frozen <c>UpdatedAt</c> as a "time in state" proxy and a fixed
    /// 15-minute timeout — which meant a runtime that was busy-but-quiet on the
    /// row got Crashed even while it was actively streaming build output. A real
    /// .NET first boot (<c>dotnet restore</c> ~5 min, then build) routinely runs
    /// well past 15 minutes of wall-clock without ever mutating the runtime row,
    /// so it tripped the timeout at secondsInState≈901, respawned, and looped
    /// forever — never reaching Online-degraded. The fix:
    /// <c>RecordRuntimeEventCommandHandler</c> now bumps
    /// <see cref="ProjectRuntime.LastBootstrapActivityAt"/> on every mid-boot
    /// event, and this branch measures the silence window from
    /// <c>(LastBootstrapActivityAt ?? UpdatedAt)</c>. As long as bootstrap events
    /// keep flowing, the runtime is provably alive and is left untouched; only a
    /// genuinely <i>silent</i> runtime — no events for the whole window — is
    /// Crashed.</para>
    ///
    /// <para><b>Why this branch still exists.</b> The heartbeat-cutoff branch
    /// requires <c>LastHeartbeatAt != null</c> — it can't see runtimes whose
    /// daemon died before sending the first beat. The reconciler also can't
    /// help: it only acts on Fly drift, and the Fly machine here is still
    /// reported as <c>started</c>. Without this branch a daemon that dies mid-boot
    /// inside an otherwise-healthy Fly VM leaves the row wedged forever.</para>
    ///
    /// <para><b>Why 10 minutes.</b> The window must exceed the longest <i>silent
    /// gap</i> between consecutive bootstrap events — not the total boot duration.
    /// The dominant silent stretch is <c>dotnet restore</c>, which can run ~5 min
    /// emitting nothing the orchestrator records; build and <c>npm install</c>
    /// stream more chatter. 10 minutes leaves comfortable headroom over the ~5 min
    /// restore gap so a healthy-but-quiet stage is never mistaken for a dead
    /// daemon, while still catching a truly silent runtime an order of magnitude
    /// faster than wall-clock-based total-time bounds would. If a future stage can
    /// be silent for longer than ~5 min, raise this in lockstep with it.</para>
    /// </summary>
    private const int BootstrapSilenceTimeoutMinutes = 10;

    /// <summary>How many times the inner loop fires per Hangfire invocation.</summary>
    private const int LoopIterations = 12;

    /// <summary>Sleep between inner-loop iterations.</summary>
    private const int LoopIntervalSeconds = 5;

    private readonly ApplicationDbContext _db;
    private readonly IClock _clock;
    private readonly ILogger<HeartbeatWatcherJob> _logger;

    public HeartbeatWatcherJob(
        ApplicationDbContext db,
        IClock clock,
        ILogger<HeartbeatWatcherJob> logger)
    {
        _db = db;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Hangfire entry point. Wraps the inner <see cref="Run(CancellationToken)"/>
    /// in a linked <see cref="CancellationTokenSource"/> with a hard 50-second
    /// budget so the job can never hold the
    /// <see cref="DisableConcurrentExecutionAttribute"/> lock past the 60-second
    /// TTL — even if a downstream call hangs forever. When the CTS trips,
    /// control returns, Hangfire releases the lock, and the next tick acquires
    /// on schedule.
    ///
    /// <para><see cref="AutomaticRetry"/> with <c>Attempts = 0</c> stops Hangfire
    /// from auto-requeuing a partially-cancelled run on top of the next scheduled
    /// tick.</para>
    ///
    /// <para><b>OperationCanceledException at the budget boundary is swallowed;
    /// real shutdown still bubbles.</b> The 12 x 5s loop intentionally outruns
    /// the 50s budget on the final <see cref="Task.Delay(TimeSpan, CancellationToken)"/>
    /// so cancellation fires while the worker is asleep — that's the planned
    /// mechanism for ending the loop. We catch only the cancellation that came
    /// from our budget CTS (i.e. not from the Hangfire shutdown token) so a real
    /// host shutdown still bubbles to Hangfire and the worker releases its lock.</para>
    /// </summary>
    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    [AutomaticRetry(Attempts = 0)]
    public async Task Run(IJobCancellationToken hangfireCt)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(hangfireCt.ShutdownToken);
        cts.CancelAfter(TimeSpan.FromSeconds(50));
        try
        {
            await Run(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !hangfireCt.ShutdownToken.IsCancellationRequested)
        {
            // Expected end-of-budget cancellation. The 12 x 5s loop intentionally
            // outruns the 50s budget on the final delay so cancellation can fire
            // while the worker is asleep. Returning cleanly lets Hangfire release
            // the DisableConcurrentExecution lock on time.
            _logger.LogDebug("HeartbeatWatcherJob hit 50s budget — returning cleanly so the lock releases on time");
        }
    }

    /// <summary>
    /// Loops 12 times with 5-second gaps so the effective scan cadence is 5 s
    /// while we keep one Hangfire registration. Per-iteration exceptions are
    /// swallowed so one bad scan doesn't kill the remaining ones; the
    /// <see cref="DisableConcurrentExecutionAttribute"/> on the entry point
    /// guards the whole minute against another worker overlapping.
    /// </summary>
    public async Task Run(CancellationToken ct = default)
    {
        for (int i = 0; i < LoopIterations && !ct.IsCancellationRequested; i++)
        {
            try
            {
                await ScanOnce(ct);
            }
            // Filter out OCE-from-our-budget so we don't log it at Error level on
            // the final iteration when the 50s budget CTS fires mid-scan. Real
            // host shutdown still bubbles to the outer try/catch as a clean
            // OperationCanceledException (handled there with LogDebug).
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "HeartbeatWatcher scan iteration {Iteration} failed", i);
            }

            if (i < LoopIterations - 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(LoopIntervalSeconds), ct);
            }
        }
    }

    /// <summary>
    /// Single scan pass. Public so tests can target it directly without the
    /// 55-second sleep budget the loop carries.
    ///
    /// <para>Two branches feed a single <c>SaveChangesAsync</c> at the end:</para>
    /// <list type="number">
    ///   <item><b>Heartbeat-cutoff</b> — runtimes in Online / Bootstrapping /
    ///         Waking whose <c>LastHeartbeatAt</c> is older than the
    ///         <see cref="ThresholdSeconds"/> silence threshold. The daemon
    ///         connected at least once and then went silent.</item>
    ///   <item><b>Bootstrap-silence</b> — runtimes in Booting / Bootstrapping /
    ///         Waking with <c>LastHeartbeatAt == null</c> whose coalesced
    ///         <c>(LastBootstrapActivityAt ?? UpdatedAt)</c> is older than the
    ///         <see cref="BootstrapSilenceTimeoutMinutes"/> silence window —
    ///         i.e. no bootstrap-progress event has landed for that long. The
    ///         daemon never connected; without this branch the row sits wedged
    ///         forever because the first branch's <c>LastHeartbeatAt != null</c>
    ///         filter and the reconciler's Fly-drift-only scope both miss it.
    ///         Measuring silence (not total time-in-state) keeps a healthy
    ///         but quiet boot — e.g. a ~5-minute <c>dotnet restore</c> — from
    ///         being Crashed mid-build.</item>
    /// </list>
    /// </summary>
    public async Task ScanOnce(CancellationToken ct = default)
    {
        var nowUtc = _clock.UtcNow;
        var cutoff = nowUtc.AddSeconds(-ThresholdSeconds);
        var bootstrapCutoff = nowUtc.AddMinutes(-BootstrapSilenceTimeoutMinutes);

        // ---- Branch 1: heartbeat-cutoff (the daemon connected then went silent) ----
        //
        // States that expect heartbeats: Online, Bootstrapping, Waking.
        // Booting (no daemon yet — handled by branch 2 when stuck), Suspended/Suspending
        // (intentionally quiet), and Pending/Crashed/Failed/Deleting/Deleted
        // (terminal/operator) skipped.
        var stale = await _db.ProjectRuntimes
            .Where(r => (r.State == RuntimeState.Online
                      || r.State == RuntimeState.Bootstrapping
                      || r.State == RuntimeState.Waking)
                     && r.LastHeartbeatAt != null
                     && r.LastHeartbeatAt < cutoff)
            .ToListAsync(ct);

        var flagged = 0;
        foreach (var runtime in stale)
        {
            var secondsSilent = (int)(nowUtc - runtime.LastHeartbeatAt!.Value).TotalSeconds;
            var metadata = JsonSerializer.Serialize(new
            {
                lastHeartbeatAt = runtime.LastHeartbeatAt,
                secondsSilent,
            });

            var result = runtime.TransitionTo(
                RuntimeState.Crashed,
                "heartbeat:missed",
                "watcher:heartbeat",
                metadata);

            if (result.IsSuccess)
            {
                flagged++;
            }
            else
            {
                _logger.LogWarning(
                    "Heartbeat watcher could not transition runtime {RuntimeId} from {State} to Crashed: {Error}",
                    runtime.Id, runtime.State, result.Error);
            }
        }

        // ---- Branch 2: bootstrap-silence (the daemon never connected and has gone quiet) ----
        //
        // The heartbeat-cutoff branch's `LastHeartbeatAt != null` filter explicitly
        // skips these rows, and RuntimeReconcilerJob only flips Crashed when Fly
        // reports drift (machine missing / stopped). When the daemon process dies
        // inside an otherwise-healthy Fly VM during bootstrap, both watchers leave
        // the row stuck in Bootstrapping / Booting / Waking forever. This branch
        // closes that gap.
        //
        // SILENCE, not time-in-state. We measure the gap since the last sign of
        // bootstrap activity, coalesced as (LastBootstrapActivityAt ?? UpdatedAt):
        //   * LastBootstrapActivityAt is bumped by RecordRuntimeEventCommandHandler
        //     on every mid-boot RuntimeEvent (clone / dotnet restore / build /
        //     install / service start). It is the authoritative proof-of-life while
        //     bootstrap progress streams to RuntimeEvents only and never touches the
        //     runtime row.
        //   * UpdatedAt is the fallback for runtimes that have not yet produced a
        //     single bootstrap event (LastBootstrapActivityAt still null) — it holds
        //     the time of the last state transition (TransitionTo + SaveChangesAsync),
        //     a clean lower bound for "time since this row last changed".
        // Using the coalesce instead of bare UpdatedAt is the fix for the
        // respawn-loop: a runtime busy-but-quiet on the row (e.g. dotnet restore for
        // ~5 min) keeps bumping LastBootstrapActivityAt, so its silence window stays
        // small and it is NOT Crashed mid-build. Only a genuinely silent runtime —
        // no events for the whole BootstrapSilenceTimeoutMinutes window — is flagged.
        // (The test suite seeds LastBootstrapActivityAt / UpdatedAt explicitly so the
        // coalesced silence proxy is contractually pinned.)
        var stuckMidBoot = await _db.ProjectRuntimes
            .Where(r => (r.State == RuntimeState.Booting
                      || r.State == RuntimeState.Bootstrapping
                      || r.State == RuntimeState.Waking)
                     && r.LastHeartbeatAt == null
                     && (r.LastBootstrapActivityAt ?? r.UpdatedAt) < bootstrapCutoff)
            .ToListAsync(ct);

        var bootstrapTimedOut = 0;
        foreach (var runtime in stuckMidBoot)
        {
            var lastActivityAt = runtime.LastBootstrapActivityAt ?? runtime.UpdatedAt;
            var secondsInState = (int)(nowUtc - lastActivityAt).TotalSeconds;
            var metadata = JsonSerializer.Serialize(new
            {
                previousState = runtime.State.ToString(),
                secondsSilent = secondsInState,
                silenceProxy = runtime.LastBootstrapActivityAt != null
                    ? "LastBootstrapActivityAt"
                    : "UpdatedAt",
            });

            var result = runtime.TransitionTo(
                RuntimeState.Crashed,
                "bootstrap:timeout",
                "watcher:bootstrap_timeout",
                metadata);

            if (result.IsSuccess)
            {
                bootstrapTimedOut++;
            }
            else
            {
                _logger.LogWarning(
                    "Heartbeat watcher could not transition stuck mid-boot runtime {RuntimeId} from {State} to Crashed: {Error}",
                    runtime.Id, runtime.State, result.Error);
            }
        }

        if (flagged == 0 && bootstrapTimedOut == 0)
        {
            return;
        }

        await _db.SaveChangesAsync(ct);

        if (flagged > 0)
        {
            _logger.LogInformation(
                "HeartbeatWatcher flagged {Count} runtimes as Crashed (cutoff={Cutoff:O}, threshold_s={Threshold})",
                flagged, cutoff, ThresholdSeconds);
        }

        if (bootstrapTimedOut > 0)
        {
            _logger.LogInformation(
                "HeartbeatWatcher flagged {Count} mid-boot runtimes as Crashed for bootstrap silence (cutoff={Cutoff:O}, silence_min={Threshold})",
                bootstrapTimedOut, bootstrapCutoff, BootstrapSilenceTimeoutMinutes);
        }
    }
}

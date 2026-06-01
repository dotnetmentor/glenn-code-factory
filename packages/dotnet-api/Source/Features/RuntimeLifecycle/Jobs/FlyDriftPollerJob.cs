using System.Text.Json;
using Hangfire;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeEvents.Commands;
using Source.Features.RuntimeEvents.Models;
using Source.Features.RuntimeLifecycle.FlySnapshot;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;

namespace Source.Features.RuntimeLifecycle.Jobs;

/// <summary>
/// Recurring Hangfire job that compares each runtime's persisted lifecycle
/// state (what <i>we</i> think) against Fly's live machine state (what
/// <i>Fly</i> thinks) and emits a <c>RuntimeFlyDriftDetected</c> observability
/// event whenever the two disagree. Audit item A4 of the
/// <c>runtime-observability-super-admin</c> card: the super-admin drawer
/// needs an authoritative source for "your DB row says Online but Fly says
/// the machine is stopped" without making the operator open the Fly UI.
///
/// <para><b>Cadence.</b> Hangfire's smallest registration granularity is one
/// minute. The job fans out internally with a one-shot scan that touches up
/// to <see cref="MaxRuntimesPerScan"/> runtimes — comfortably above expected
/// active counts — and concurrency-disables per Hangfire run so we never
/// have two overlapping polls on the same minute. Per-runtime Fly polling is
/// throttled by the soft cap below, not by sleep loops: the goal is "one
/// pass per minute", not "one runtime every N seconds".</para>
///
/// <para><b>Scope.</b> Only runtimes that have a Fly machine to compare
/// against are scanned: <c>FlyMachineId != null</c>. We additionally exclude
/// the <c>Pending</c> / <c>Deleting</c> / <c>Deleted</c> states — Pending
/// runtimes haven't been provisioned yet, and the delete path is
/// intentionally tearing the Fly machine down so a drift event during it
/// would be noise.</para>
///
/// <para><b>Drift definition.</b> A runtime is drifted when our normalised
/// state vs Fly's reported state belong to different "logical buckets" — see
/// <see cref="IsDriftBetween"/>. We deliberately don't compare strings 1:1
/// because Fly and we use different vocabularies (Fly: started/stopped/
/// suspended; us: Online/Crashed/Suspended) and a 1:1 match would either
/// over-fire or never fire.</para>
///
/// <para><b>Best-effort emission.</b> The job is observability, not a
/// reconciler — it does NOT mutate any runtime state, it only records that
/// drift was observed. The downstream reconciler (separate job) is the
/// authority on actually correcting state. Event-record failures are
/// swallowed with a warning so one bad row can't kill the scan.</para>
/// </summary>
public class FlyDriftPollerJob
{
    /// <summary>
    /// Soft cap on runtimes scanned per Hangfire invocation. Picked well above
    /// expected active runtime counts but bounded so a runaway upstream
    /// (e.g. Fly responding very slowly) can't hold the worker for an
    /// unbounded window before the next scheduled tick wants it back.
    /// </summary>
    public const int MaxRuntimesPerScan = 200;

    private readonly ApplicationDbContext _db;
    private readonly IRuntimeFlySnapshotService _snapshot;
    private readonly IMediator _mediator;
    private readonly ILogger<FlyDriftPollerJob> _logger;

    public FlyDriftPollerJob(
        ApplicationDbContext db,
        IRuntimeFlySnapshotService snapshot,
        IMediator mediator,
        ILogger<FlyDriftPollerJob> logger)
    {
        _db = db;
        _snapshot = snapshot;
        _mediator = mediator;
        _logger = logger;
    }

    /// <summary>
    /// Hangfire entry point. Wraps the scan in a linked
    /// <see cref="CancellationTokenSource"/> with a hard 50-second budget so
    /// the job never holds the <see cref="DisableConcurrentExecutionAttribute"/>
    /// lock past its TTL even if Fly hangs on a single call.
    /// <see cref="AutomaticRetry"/> with <c>Attempts = 0</c> stops Hangfire
    /// from auto-requeuing on top of the next scheduled tick — drift
    /// detection is idempotent, the next tick re-tries naturally.
    /// </summary>
    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    [AutomaticRetry(Attempts = 0)]
    public async Task Run(IJobCancellationToken hangfireCt)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(hangfireCt.ShutdownToken);
        cts.CancelAfter(TimeSpan.FromSeconds(50));
        await Run(cts.Token);
    }

    /// <summary>
    /// Single scan pass. Public so tests can target it directly without going
    /// through the Hangfire IJobCancellationToken adapter.
    /// </summary>
    public async Task Run(CancellationToken ct = default)
    {
        var candidates = await _db.ProjectRuntimes
            .AsNoTracking()
            .Where(r => r.FlyMachineId != null
                     && r.State != RuntimeState.Pending
                     && r.State != RuntimeState.Deleting
                     && r.State != RuntimeState.Deleted)
            .OrderBy(r => r.Id)
            .Take(MaxRuntimesPerScan)
            .Select(r => new { r.Id, r.State, r.FlyMachineId })
            .ToListAsync(ct);

        if (candidates.Count == 0)
        {
            return;
        }

        var driftCount = 0;
        foreach (var candidate in candidates)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var snapshot = await _snapshot.GetAsync(candidate.Id, ct);
                if (snapshot is null)
                {
                    // Runtime vanished between the listing and the snapshot
                    // call — fine, the next tick won't even include it.
                    continue;
                }

                // Fly half couldn't be resolved (machine 404 / API blew up /
                // transport failure). The snapshot service already logged the
                // cause at the appropriate level. We don't emit drift for
                // "Fly unreachable" because that's an outage signal, not a
                // state-disagreement signal — the dedicated Fly outage
                // observability lives in the FlyOperations timeline.
                if (snapshot.FlyView is null)
                {
                    continue;
                }

                if (IsDriftBetween(candidate.State, snapshot.FlyView.State))
                {
                    await EmitDriftAsync(
                        runtimeId: candidate.Id,
                        ourState: candidate.State,
                        flyState: snapshot.FlyView.State,
                        flyMachineId: candidate.FlyMachineId,
                        ct: ct);
                    driftCount++;
                }
            }
            catch (Exception ex)
            {
                // One bad runtime must not break the scan. Log and continue.
                _logger.LogWarning(
                    ex,
                    "FlyDriftPoller: snapshot/compare failed for runtime {RuntimeId}; skipping.",
                    candidate.Id);
            }
        }

        _logger.LogInformation(
            "FlyDriftPoller: scanned {Count} runtimes, recorded drift on {DriftCount}.",
            candidates.Count, driftCount);
    }

    /// <summary>
    /// Bucket-comparison between our persisted state and Fly's reported
    /// machine state. Returns true when the two belong to different logical
    /// groups. The buckets are:
    /// <list type="bullet">
    ///   <item><b>Running</b>: we say <c>Online</c> / <c>Bootstrapping</c> /
    ///         <c>Waking</c> / <c>Booting</c>; Fly says <c>started</c>.</item>
    ///   <item><b>Stopped</b>: we say <c>Suspended</c> / <c>Suspending</c>;
    ///         Fly says <c>stopped</c> / <c>suspended</c> / <c>stopping</c>.</item>
    ///   <item><b>Crashed</b>: we say <c>Crashed</c> / <c>Failed</c>; Fly says
    ///         anything (often <c>started</c> if the machine is up but the
    ///         daemon died, or <c>stopped</c> after a Fly-side OOM).</item>
    /// </list>
    /// Anything that falls outside the bucket map (unknown Fly state strings,
    /// transitional Fly states like <c>created</c>) is conservatively reported
    /// as non-drifted — operators would rather under-fire here than be flooded
    /// during normal lifecycle moves.
    /// </summary>
    public static bool IsDriftBetween(RuntimeState ourState, string? flyState)
    {
        if (string.IsNullOrWhiteSpace(flyState))
        {
            return false;
        }

        var ourBucket = ClassifyOurState(ourState);
        var flyBucket = ClassifyFlyState(flyState);

        // Unknowns: don't fire. We prefer a missed drift over a false-positive
        // storm during state transitions (e.g. Fly briefly reporting "created"
        // mid-spin-up).
        if (ourBucket == StateBucket.Unknown || flyBucket == StateBucket.Unknown)
        {
            return false;
        }

        return ourBucket != flyBucket;
    }

    private enum StateBucket { Unknown, Running, Stopped, Crashed }

    private static StateBucket ClassifyOurState(RuntimeState state) => state switch
    {
        RuntimeState.Online or
        RuntimeState.Bootstrapping or
        RuntimeState.Waking or
        RuntimeState.Booting => StateBucket.Running,
        RuntimeState.Suspended or
        RuntimeState.Suspending => StateBucket.Stopped,
        RuntimeState.Crashed or
        RuntimeState.Failed => StateBucket.Crashed,
        _ => StateBucket.Unknown,
    };

    private static StateBucket ClassifyFlyState(string flyState) => flyState.ToLowerInvariant() switch
    {
        "started" => StateBucket.Running,
        "stopped" or "stopping" or "suspended" => StateBucket.Stopped,
        // Fly doesn't have a "Crashed" state — a daemon crash typically leaves
        // the machine in `started`, which our "Crashed" bucket disagrees with
        // and that disagreement IS the drift we want to surface. So we never
        // map a Fly string into Crashed; the comparison fires correctly.
        _ => StateBucket.Unknown,
    };

    /// <summary>
    /// Emit a <c>RuntimeFlyDriftDetected</c> runtime event. Same best-effort
    /// contract as the rest of the observability path: a record failure logs
    /// a warning and is swallowed so a single bad emit doesn't poison the
    /// scan.
    /// </summary>
    private async Task EmitDriftAsync(
        Guid runtimeId,
        RuntimeState ourState,
        string flyState,
        string? flyMachineId,
        CancellationToken ct)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                ourState = ourState.ToString(),
                flyState,
                flyMachineId,
            });

            await _mediator.Send(
                new RecordRuntimeEventCommand(
                    RuntimeId: runtimeId,
                    Type: RuntimeEventTypes.RuntimeFlyDriftDetected,
                    Severity: RuntimeEventSeverity.Warn,
                    Timestamp: DateTime.UtcNow,
                    DurationMs: null,
                    Payload: payload),
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "FlyDriftPoller: RuntimeFlyDriftDetected emit failed for runtime {RuntimeId}; drift will be re-detected on the next tick.",
                runtimeId);
        }
    }
}

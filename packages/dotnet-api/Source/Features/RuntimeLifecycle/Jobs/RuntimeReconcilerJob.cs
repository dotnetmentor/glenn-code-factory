using Hangfire;
using Microsoft.EntityFrameworkCore;
using Source.Features.FlyManagement;
using Source.Features.FlyManagement.Models;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;

namespace Source.Features.RuntimeLifecycle.Jobs;

/// <summary>
/// Recurring Hangfire job that compares what the database thinks each
/// <see cref="ProjectRuntime"/> is doing to what Fly actually reports, and
/// nudges drifted rows back onto the rails via the existing
/// <see cref="RuntimeStateMachine"/>. Runs every minute via
/// <see cref="RuntimeReconcilerJobRegistration"/>.
///
/// <para><b>Scope.</b> This is a thin <i>drift fixer</i> — not a state rebuilder.
/// We trust the webhook + provisioner pipeline to drive the happy path; the
/// reconciler exists to close the small gaps that pipeline leaves: a missed
/// webhook, a Fly-side state that diverged silently, a machine that vanished
/// from Fly entirely. Anything we can't legally transition, we log and leave
/// alone — the next tick (or a fresh webhook) will close the gap.</para>
///
/// <para><b>Concurrency.</b> Decorated with
/// <see cref="DisableConcurrentExecutionAttribute"/> so two Hangfire workers
/// can't both reconcile the same row. The 120-second timeout gives the
/// <c>ListMachinesAsync</c> round-trip plus per-row state-machine work
/// comfortable headroom even in a degraded Fly window before a stuck worker is
/// allowed to be stepped on by a fresh one.</para>
///
/// <para><b>Mapping consistency.</b> The (Fly state, runtime state) → target
/// mapping below is intentionally aligned with
/// <c>HandleFlyMachineStateChangedHandler</c>. Where the webhook handler emits
/// only the canonical legal transition for a given Fly event, the reconciler
/// covers the same ground <i>plus</i> a couple of drift-only entries (e.g. an
/// <c>Online</c> runtime whose Fly machine is already <c>stopped</c>). Those
/// drift entries always pick the <i>closest legal target</i> in the state
/// graph; if the next legal hop is multiple edges away (Online → Suspending →
/// Suspended), we take the first edge and let the next tick close the rest.</para>
///
/// <para><b>Failure isolation.</b> We catch <see cref="FlyApiException"/> at
/// the top of the job: if Fly is down or rate-limiting, the reconciler logs a
/// warning and returns clean. Per-row transition failures are caught
/// individually so one bad row can't poison the batch.</para>
/// </summary>
public class RuntimeReconcilerJob
{
    private readonly ApplicationDbContext _db;
    private readonly FlyClient _fly;
    private readonly ILogger<RuntimeReconcilerJob> _logger;

    public RuntimeReconcilerJob(
        ApplicationDbContext db,
        FlyClient fly,
        ILogger<RuntimeReconcilerJob> logger)
    {
        _db = db;
        _fly = fly;
        _logger = logger;
    }

    /// <summary>
    /// Hangfire entry point. Wraps the inner <see cref="Run(CancellationToken)"/>
    /// in a linked <see cref="CancellationTokenSource"/> with a hard 110-second
    /// budget so the job can never hold the
    /// <see cref="DisableConcurrentExecutionAttribute"/> lock past the 120-second
    /// TTL — even if a Fly call hangs forever. When the CTS trips, control
    /// returns, Hangfire releases the lock, and the next tick acquires on
    /// schedule.
    ///
    /// <para><see cref="AutomaticRetry"/> with <c>Attempts = 0</c> stops Hangfire
    /// from auto-requeuing a partially-cancelled run on top of the next scheduled
    /// tick.</para>
    /// </summary>
    [DisableConcurrentExecution(timeoutInSeconds: 120)]
    [AutomaticRetry(Attempts = 0)]
    public async Task Run(IJobCancellationToken hangfireCt)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(hangfireCt.ShutdownToken);
        cts.CancelAfter(TimeSpan.FromSeconds(110));
        await Run(cts.Token);
    }

    /// <summary>
    /// Process one reconciliation pass. The
    /// <see cref="DisableConcurrentExecutionAttribute"/> on the entry point
    /// guards against two workers running this method at the same time across
    /// the cluster.
    /// </summary>
    public async Task Run(CancellationToken ct = default)
    {
        // ---- 1. Pull Fly's view once per pass ----
        List<FlyMachine> machines;
        try
        {
            machines = await _fly.ListMachinesAsync(ct);
        }
        catch (FlyApiException ex)
        {
            // Fly itself rejected the list call — log and bail. Next tick will retry.
            _logger.LogWarning(
                ex,
                "RuntimeReconcilerJob: Fly ListMachines failed (status={StatusCode} code={ErrorCode}); skipping pass",
                ex.StatusCode, ex.ErrorCode);
            return;
        }

        var byMachineId = machines.ToDictionary(m => m.Id, m => m);

        // ---- 2. Pull our view of runtimes that *should* have a Fly counterpart ----
        // Skip Pending (provisioner hasn't created Fly resources yet) and Deleted
        // (terminal — the Fly side is gone on purpose). Track the rows so we can
        // mutate them in place; one SaveChanges at the end batches the domain events.
        var runtimes = await _db.ProjectRuntimes
            .AsTracking()
            .Where(r => r.State != RuntimeState.Pending
                     && r.State != RuntimeState.Deleted
                     && r.FlyMachineId != null)
            .ToListAsync(ct);

        if (runtimes.Count == 0)
        {
            _logger.LogInformation(
                "RuntimeReconcilerJob: no runtimes to scan (fly_machines={FlyCount})",
                machines.Count);
            return;
        }

        var driftFixed = 0;
        var illegalSkipped = 0;
        var stopRetried = 0;

        foreach (var runtime in runtimes)
        {
            if (!byMachineId.TryGetValue(runtime.FlyMachineId!, out var flyMachine))
            {
                // Fly has lost the machine. The right target depends on what the
                // runtime was doing: a Suspending runtime with no machine is a
                // suspend that completed successfully, a Deleting runtime with no
                // machine is a destroy that completed successfully, and only
                // running/booting states represent an unexpected disappearance
                // that should be marked Crashed (so the respawn supervisor kicks
                // in). Terminal-ish states (Suspended/Failed/Crashed) are left
                // alone — flipping them again would churn the audit trail with
                // no semantic change.
                var (missingTarget, reason) = runtime.State switch
                {
                    RuntimeState.Suspending => (RuntimeState.Suspended, "reconciler:suspend_completed_machine_missing"),
                    RuntimeState.Deleting => (RuntimeState.Deleted, "reconciler:delete_completed_machine_missing"),
                    RuntimeState.Online
                        or RuntimeState.Booting
                        or RuntimeState.Bootstrapping
                        or RuntimeState.Waking
                        => (RuntimeState.Crashed, "reconciler:machine_missing"),
                    _ => (runtime.State, string.Empty), // Suspended/Failed/Crashed/Pending/Deleted: no-op
                };

                if (missingTarget == runtime.State)
                {
                    _logger.LogDebug(
                        "RuntimeReconcilerJob: runtime {RuntimeId} in terminal state {State} has missing Fly machine — no action",
                        runtime.Id, runtime.State);
                    continue;
                }

                if (RuntimeStateMachine.CanTransition(runtime.State, missingTarget))
                {
                    var result = runtime.TransitionTo(missingTarget, reason, "reconciler", metadata: null);

                    if (result.IsSuccess)
                    {
                        driftFixed++;
                    }
                    else
                    {
                        illegalSkipped++;
                        _logger.LogWarning(
                            "RuntimeReconcilerJob: would have transitioned runtime {RuntimeId} {From} -> {To} ({Reason}) but state machine rejected: {Error}",
                            runtime.Id, runtime.State, missingTarget, reason, result.Error);
                    }
                }
                else
                {
                    illegalSkipped++;
                    _logger.LogWarning(
                        "RuntimeReconcilerJob: runtime {RuntimeId} state {State} cannot legally transition to {Target}; machine {MachineId} missing on Fly",
                        runtime.Id, runtime.State, missingTarget, runtime.FlyMachineId);
                }
                continue;
            }

            // Stuck-Suspending recovery: DB says Suspending but Fly machine is
            // still started/starting. This is the exact drift created when
            // something flipped the runtime to Suspending without (or before)
            // the Fly StopMachine call landed — e.g. branch archive that wrote
            // state but failed to call Fly, or a transient FlyApiException
            // swallowed by IdlerJob/ArchiveBranch. The state graph forbids
            // Suspending → Online so we can't "undo" the DB row; we just retry
            // the missing side-effect (the actual StopMachine call). On
            // success, Fly emits a `stopped` event which the next pass / webhook
            // closes by transitioning Suspending → Suspended via the normal
            // mapping above.
            var fly = (flyMachine.State ?? string.Empty).ToLowerInvariant();
            if (runtime.State == RuntimeState.Suspending
                && (fly == "started" || fly == "starting"))
            {
                try
                {
                    await _fly.StopMachineAsync(
                        machineId: runtime.FlyMachineId!,
                        options: null,
                        runtimeId: runtime.Id,
                        ct: ct);
                    stopRetried++;
                    _logger.LogInformation(
                        "RuntimeReconcilerJob: retried StopMachine for stuck-Suspending runtime {RuntimeId} (machine {MachineId} state={FlyState})",
                        runtime.Id, runtime.FlyMachineId, flyMachine.State);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "RuntimeReconcilerJob: StopMachine retry failed for stuck-Suspending runtime {RuntimeId} (machine {MachineId}); will retry next tick",
                        runtime.Id, runtime.FlyMachineId);
                }
                // Either way, no DB transition this tick — fall through to the
                // next runtime. The next pass either sees fly:stopped (which
                // MapDriftTarget closes via Suspending → Suspended) or sees
                // started again and retries.
                continue;
            }

            var target = MapDriftTarget(flyMachine.State, runtime.State);
            if (target is null || target == runtime.State)
            {
                // Either no opinion, or DB already matches Fly — leave it alone.
                continue;
            }

            if (!RuntimeStateMachine.CanTransition(runtime.State, target.Value))
            {
                illegalSkipped++;
                _logger.LogWarning(
                    "RuntimeReconcilerJob: would have transitioned runtime {RuntimeId} {From} -> {To} (fly_state={FlyState}) but state machine rejected",
                    runtime.Id, runtime.State, target.Value, flyMachine.State);
                continue;
            }

            var metadata = $"{{\"flyState\":\"{flyMachine.State}\",\"dbState\":\"{runtime.State}\"}}";
            var transitionResult = runtime.TransitionTo(
                target.Value,
                "reconciler:drift",
                "reconciler",
                metadata);

            if (transitionResult.IsSuccess)
            {
                driftFixed++;
            }
            else
            {
                // Defensive: CanTransition just said yes; getting here means the
                // graph and the entity disagree, which is a real bug. Log and skip.
                illegalSkipped++;
                _logger.LogWarning(
                    "RuntimeReconcilerJob: TransitionTo rejected for runtime {RuntimeId} {From} -> {To} despite CanTransition=true: {Error}",
                    runtime.Id, runtime.State, target.Value, transitionResult.Error);
            }
        }

        if (driftFixed > 0)
        {
            await _db.SaveChangesAsync(ct);
        }

        _logger.LogInformation(
            "RuntimeReconcilerJob scanned {Count} runtimes, fixed {Drift} drift, retried StopMachine on {StopRetried} stuck-Suspending, skipped {Illegal} illegal-transition cases",
            runtimes.Count, driftFixed, stopRetried, illegalSkipped);
    }

    /// <summary>
    /// Pick the runtime state we want, given Fly's reported state and the
    /// runtime's current state. Returns <c>null</c> when the reconciler has no
    /// opinion (Fly state we don't react to, or DB already matches).
    ///
    /// <para>This mirrors the canonical mapping in
    /// <c>HandleFlyMachineStateChangedHandler</c> but is allowed to take an
    /// extra step that the webhook handler doesn't (because the webhook
    /// handler runs <i>per event</i>, while we're closing accumulated drift).
    /// Any drift target we pick must still be legal under
    /// <see cref="RuntimeStateMachine"/>; the caller validates with
    /// <c>CanTransition</c> before committing.</para>
    ///
    /// <para><b>Spec note (Online → Suspended).</b> The card asks for
    /// <c>fly:stopped + db:Online → Suspended</c>, but the state graph forbids
    /// that single edge — Online must go through Suspending first. We pick
    /// <see cref="RuntimeState.Suspending"/> (the closest legal target) and
    /// rely on the next reconciler tick (or a webhook landing) to close the
    /// remaining Suspending → Suspended hop.</para>
    ///
    /// <para><b>Mid-boot drift (Booting / Bootstrapping / Waking + fly:stopped).</b>
    /// If Fly reports the machine as stopped/suspended while the DB still thinks
    /// the runtime is partway through coming up, the machine went down before
    /// it finished booting — typically a daemon bootstrap that failed so hard
    /// the supervisor never recovered (e.g. "no model slug configured"
    /// non-recoverable error in bootstrap-opencode). We mark
    /// <see cref="RuntimeState.Crashed"/> so <c>ScheduleRespawnHandler</c>
    /// kicks in and destroys + recreates a fresh machine instead of leaving
    /// the reconciler logging drift events forever. Booting / Bootstrapping /
    /// Waking → Crashed are all legal in <see cref="RuntimeStateMachine"/>.</para>
    /// </summary>
    private static RuntimeState? MapDriftTarget(string flyState, RuntimeState currentState)
    {
        var fly = (flyState ?? string.Empty).ToLowerInvariant();

        return (fly, currentState) switch
        {
            // ----- "started" lines up with Online -----
            ("started", RuntimeState.Booting) => RuntimeState.Bootstrapping,
            // Waking + fly:started means "Fly machine is up but daemon hasn't
            // confirmed yet". We hand off to Bootstrapping; only the daemon's
            // RuntimeReady hub call is allowed to flip Bootstrapping → Online.
            // (Daemon-as-downloadable: the cold-boot now includes a tarball
            // download + extract step, so Online can't be assumed from machine
            // state alone.)
            ("started", RuntimeState.Waking) => RuntimeState.Bootstrapping,

            // ----- "stopped"/"suspended" lines up with Suspended -----
            ("stopped", RuntimeState.Suspending) => RuntimeState.Suspended,
            ("suspended", RuntimeState.Suspending) => RuntimeState.Suspended,
            // Drift: Fly stopped the machine but we still think it's Online (missed
            // a Suspending transition). Closest legal hop is Suspending; the next
            // tick will pick up Suspending -> Suspended.
            ("stopped", RuntimeState.Online) => RuntimeState.Suspending,
            ("suspended", RuntimeState.Online) => RuntimeState.Suspending,

            // ----- Fly stopped/suspended while DB still says mid-boot -----
            // The machine went down while we expected it to be coming up. Mark Crashed
            // so ScheduleRespawnHandler kicks in (destroy + recreate fresh machine).
            // The legality check (CanTransition) at the call site catches any state
            // graph mismatch.
            ("stopped", RuntimeState.Booting) => RuntimeState.Crashed,
            ("stopped", RuntimeState.Bootstrapping) => RuntimeState.Crashed,
            ("stopped", RuntimeState.Waking) => RuntimeState.Crashed,
            ("suspended", RuntimeState.Booting) => RuntimeState.Crashed,
            ("suspended", RuntimeState.Bootstrapping) => RuntimeState.Crashed,
            ("suspended", RuntimeState.Waking) => RuntimeState.Crashed,

            // ----- "destroyed" -----
            ("destroyed", RuntimeState.Suspending) => RuntimeState.Suspended,
            ("destroyed", RuntimeState.Deleting) => RuntimeState.Deleted,

            // ----- "crashed"/"failed" -----
            ("crashed", RuntimeState.Online) => RuntimeState.Crashed,
            ("crashed", RuntimeState.Bootstrapping) => RuntimeState.Crashed,
            ("crashed", RuntimeState.Booting) => RuntimeState.Crashed,
            ("crashed", RuntimeState.Waking) => RuntimeState.Crashed,
            ("failed", RuntimeState.Online) => RuntimeState.Crashed,
            ("failed", RuntimeState.Bootstrapping) => RuntimeState.Crashed,
            ("failed", RuntimeState.Booting) => RuntimeState.Crashed,
            ("failed", RuntimeState.Waking) => RuntimeState.Crashed,

            _ => null,
        };
    }
}

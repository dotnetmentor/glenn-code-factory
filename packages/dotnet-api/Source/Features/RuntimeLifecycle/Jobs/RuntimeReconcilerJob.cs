using Hangfire;
using Microsoft.EntityFrameworkCore;
using Source.Features.BoxManagement;
using Source.Features.BoxManagement.Models;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;

namespace Source.Features.RuntimeLifecycle.Jobs;

/// <summary>
/// Recurring Hangfire job that compares what the database thinks each
/// <see cref="ProjectRuntime"/> is doing to what Box actually reports, and
/// nudges drifted rows back onto the rails via the existing
/// <see cref="RuntimeStateMachine"/>. Runs every minute via
/// <see cref="RuntimeReconcilerJobRegistration"/>.
///
/// <para><b>Scope — bigger than the Box era.</b> Box has no webhooks, so the
/// reconciler is not just a drift fixer any more: it is the ONLY driver of the
/// box-observation edges (Booting → Bootstrapping when the VM comes up,
/// Suspending → Suspended when the stop lands). The daemon's own
/// <c>RuntimeReady</c> hub call still owns Bootstrapping → Online; the
/// reconciler never assumes Online from VM state alone.</para>
///
/// <para><b>Concurrency.</b> <see cref="DisableConcurrentExecutionAttribute"/>
/// so two workers can't both reconcile the same row; 120-second timeout gives
/// the <c>ListBoxesAsync</c> round-trip plus per-row work comfortable headroom
/// even in a degraded Box window.</para>
///
/// <para><b>Failure isolation.</b> <see cref="BoxApiException"/> at the top of
/// the pass logs a warning and returns clean; per-row transition failures are
/// caught individually so one bad row can't poison the batch.</para>
/// </summary>
public class RuntimeReconcilerJob
{
    private readonly ApplicationDbContext _db;
    private readonly BoxClient _box;
    private readonly ILogger<RuntimeReconcilerJob> _logger;

    public RuntimeReconcilerJob(
        ApplicationDbContext db,
        BoxClient box,
        ILogger<RuntimeReconcilerJob> logger)
    {
        _db = db;
        _box = box;
        _logger = logger;
    }

    /// <summary>
    /// Hangfire entry point. Wraps the inner <see cref="Run(CancellationToken)"/>
    /// in a linked CTS with a hard 110-second budget so the job can never hold the
    /// lock past the 120-second TTL — even if a Box call hangs forever.
    /// </summary>
    [DisableConcurrentExecution(timeoutInSeconds: 120)]
    [AutomaticRetry(Attempts = 0)]
    public async Task Run(IJobCancellationToken hangfireCt)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(hangfireCt.ShutdownToken);
        cts.CancelAfter(TimeSpan.FromSeconds(110));
        await Run(cts.Token);
    }

    /// <summary>Process one reconciliation pass.</summary>
    public async Task Run(CancellationToken ct = default)
    {
        // ---- 1. Pull Box's view once per pass ----
        List<BoxVm> boxes;
        try
        {
            boxes = await _box.ListBoxesAsync(ct);
        }
        catch (BoxApiException ex)
        {
            _logger.LogWarning(
                ex,
                "RuntimeReconcilerJob: Box ListBoxes failed (status={StatusCode} code={ErrorCode}); skipping pass",
                ex.StatusCode, ex.ErrorCode);
            return;
        }

        var byBoxId = boxes.ToDictionary(b => b.Id, b => b);

        // ---- 2. Pull our view of runtimes that *should* have a box ----
        // Skip Pending (provisioner hasn't forked yet) and Deleted (terminal —
        // the box is gone on purpose).
        var runtimes = await _db.ProjectRuntimes
            .AsTracking()
            .Where(r => r.State != RuntimeState.Pending
                     && r.State != RuntimeState.Deleted
                     && r.BoxId != null)
            .ToListAsync(ct);

        if (runtimes.Count == 0)
        {
            _logger.LogInformation(
                "RuntimeReconcilerJob: no runtimes to scan (boxes={BoxCount})",
                boxes.Count);
            return;
        }

        var driftFixed = 0;
        var illegalSkipped = 0;
        var stopRetried = 0;

        foreach (var runtime in runtimes)
        {
            if (!byBoxId.TryGetValue(runtime.BoxId!, out var boxVm))
            {
                // Box has lost the VM (deleted, or TTL'd + purged). The right target
                // depends on what the runtime was doing: Deleting with no box is a
                // delete that completed; Suspending with no box means someone deleted
                // it out from under us — treat as suspend-complete (the DB row keeps
                // the id; a wake will 404 and re-fork). Live states mean an
                // unexpected disappearance → Crashed so the respawn supervisor
                // kicks in. Terminal-ish states are left alone.
                var (missingTarget, reason) = runtime.State switch
                {
                    RuntimeState.Suspending => (RuntimeState.Suspended, "reconciler:suspend_completed_box_missing"),
                    RuntimeState.Deleting => (RuntimeState.Deleted, "reconciler:delete_completed_box_missing"),
                    RuntimeState.Online
                        or RuntimeState.Booting
                        or RuntimeState.Bootstrapping
                        or RuntimeState.Waking
                        => (RuntimeState.Crashed, "reconciler:box_missing"),
                    _ => (runtime.State, string.Empty), // Suspended/Failed/Crashed: no-op
                };

                if (missingTarget == runtime.State)
                {
                    _logger.LogDebug(
                        "RuntimeReconcilerJob: runtime {RuntimeId} in terminal state {State} has missing box — no action",
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
                        "RuntimeReconcilerJob: runtime {RuntimeId} state {State} cannot legally transition to {Target}; box {BoxId} missing",
                        runtime.Id, runtime.State, missingTarget, runtime.BoxId);
                }
                continue;
            }

            // Stuck-Suspending recovery: DB says Suspending but the box is still up.
            // This is the drift created when something flipped the runtime to
            // Suspending without (or before) the StopBox call landed. The state
            // graph forbids Suspending → Online, so we retry the missing side-effect
            // (the actual stop). On success the next pass observes `archived` and
            // closes Suspending → Suspended via the normal mapping.
            if (runtime.State == RuntimeState.Suspending && BoxStates.IsUp(boxVm.State))
            {
                try
                {
                    await _box.StopBoxAsync(
                        boxId: runtime.BoxId!,
                        runtimeId: runtime.Id,
                        ct: ct);
                    stopRetried++;
                    _logger.LogInformation(
                        "RuntimeReconcilerJob: retried StopBox for stuck-Suspending runtime {RuntimeId} (box {BoxId} state={State})",
                        runtime.Id, runtime.BoxId, boxVm.State);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "RuntimeReconcilerJob: StopBox retry failed for stuck-Suspending runtime {RuntimeId} (box {BoxId}); will retry next tick",
                        runtime.Id, runtime.BoxId);
                }
                continue;
            }

            var target = MapDriftTarget(boxVm.State, runtime.State);
            if (target is null || target == runtime.State)
            {
                continue;
            }

            if (!RuntimeStateMachine.CanTransition(runtime.State, target.Value))
            {
                illegalSkipped++;
                _logger.LogWarning(
                    "RuntimeReconcilerJob: would have transitioned runtime {RuntimeId} {From} -> {To} (box_state={BoxState}) but state machine rejected",
                    runtime.Id, runtime.State, target.Value, boxVm.State);
                continue;
            }

            var metadata = $"{{\"boxState\":\"{boxVm.State}\",\"dbState\":\"{runtime.State}\"}}";
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
            "RuntimeReconcilerJob scanned {Count} runtimes, fixed {Drift} drift, retried StopBox on {StopRetried} stuck-Suspending, skipped {Illegal} illegal-transition cases",
            runtimes.Count, driftFixed, stopRetried, illegalSkipped);
    }

    /// <summary>
    /// Pick the runtime state we want, given the box's reported <c>state</c> and
    /// the runtime's current state. Returns <c>null</c> when the reconciler has no
    /// opinion (a state we don't react to, or DB already matches).
    ///
    /// <para>Box's state vocabulary (per the OpenAPI contract): up
    /// (<c>ready</c>/<c>idle</c>/<c>running</c>), transitional
    /// (<c>init</c>/<c>provisioning</c>/<c>provisioned</c>/<c>cloning</c>/
    /// <c>archiving</c>), stopped-with-snapshot (<c>archived</c>), broken
    /// (<c>error</c>). The daemon's <c>RuntimeReady</c> hub call remains the ONLY
    /// path to Online — a VM being up says nothing about the daemon having
    /// bootstrapped.</para>
    ///
    /// <para><b>Spec note (Online → Suspended).</b> <c>archived</c> + db:Online
    /// takes the closest legal edge (Suspending); the next tick closes
    /// Suspending → Suspended. Mirrors the old Fly mapping.</para>
    ///
    /// <para><b>Mid-boot drift (Booting/Bootstrapping/Waking + archived/error).</b>
    /// The box went down (or errored) before the daemon confirmed — mark Crashed so
    /// <c>ScheduleRespawnHandler</c> kicks in and the respawn reboots the box.</para>
    /// </summary>
    private static RuntimeState? MapDriftTarget(string boxState, RuntimeState currentState)
    {
        var state = (boxState ?? string.Empty).ToLowerInvariant();
        var up = BoxStates.IsUp(state);
        var archived = BoxStates.IsArchived(state);
        var error = BoxStates.IsError(state);

        // ----- box is up: the VM exists and runs, daemon confirmation pending -----
        if (up)
        {
            return currentState switch
            {
                RuntimeState.Booting => RuntimeState.Bootstrapping,
                // Waking + up means "box resumed but daemon hasn't confirmed yet".
                // Hand off to Bootstrapping; only the daemon's RuntimeReady hub
                // call is allowed to flip Bootstrapping → Online.
                RuntimeState.Waking => RuntimeState.Bootstrapping,
                _ => null,
            };
        }

        // ----- box is archived (stopped with snapshot) -----
        if (archived)
        {
            return currentState switch
            {
                RuntimeState.Suspending => RuntimeState.Suspended,
                // Drift: the box archived itself (TTL guardrail!) or someone stopped
                // it out-of-band while we think it's Online. Closest legal hop is
                // Suspending; the next tick closes Suspending → Suspended.
                RuntimeState.Online => RuntimeState.Suspending,
                // Went down mid-boot → Crashed so the respawn supervisor reboots it.
                RuntimeState.Booting => RuntimeState.Crashed,
                RuntimeState.Bootstrapping => RuntimeState.Crashed,
                RuntimeState.Waking => RuntimeState.Crashed,
                _ => null,
            };
        }

        // ----- box-side hard failure -----
        if (error)
        {
            return currentState switch
            {
                RuntimeState.Online => RuntimeState.Crashed,
                RuntimeState.Booting => RuntimeState.Crashed,
                RuntimeState.Bootstrapping => RuntimeState.Crashed,
                RuntimeState.Waking => RuntimeState.Crashed,
                // Suspending + error: the stop failed hard box-side. Suspended is
                // the closest truthful terminal for the DB; a later wake surfaces
                // the error again and the respawn path recovers.
                RuntimeState.Suspending => RuntimeState.Suspended,
                _ => null,
            };
        }

        // transitional (init/provisioning/provisioned/cloning/archiving — or an
        // unknown future state): no opinion — let it settle.
        return null;
    }
}

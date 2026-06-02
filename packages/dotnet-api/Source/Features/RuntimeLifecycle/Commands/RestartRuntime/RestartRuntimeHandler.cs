using Hangfire;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Source.Features.FlyManagement;
using Source.Features.RuntimeLifecycle.Jobs;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimeLifecycle.Commands.RestartRuntime;

/// <summary>
/// Handler for <see cref="RestartRuntimeCommand"/>.
///
/// <list type="bullet">
///   <item>Resolves the most-recent (non-deleted) <see cref="ProjectRuntime"/>
///         for the <c>(ProjectId, BranchId)</c> pair — the global query filter
///         on <c>ProjectRuntime</c> already excludes soft-deleted rows, so
///         operator <c>force-delete</c>d runtimes naturally collapse to 404.</item>
///   <item>Pre-checks the state and dispatches by branch:
///         <list type="bullet">
///           <item><see cref="RuntimeState.Suspended"/> → delegate to
///                 <see cref="WakeRuntimeOnConnectCommand"/>. The machine +
///                 volume are intact; Fly just needs to start the VM. No need
///                 to tear the runtime down and re-provision.</item>
///           <item><see cref="RuntimeState.Failed"/> or
///                 <see cref="RuntimeState.Crashed"/> → full restart path
///                 below (recreate the machine on the existing volume).</item>
///           <item>Anything else → <see cref="ConflictPrefix"/> so the
///                 controller maps to 409 with a state-aware error.</item>
///         </list></item>
///   <item>Restart path: calls <see cref="ProjectRuntime.Restart"/>, which
///         validates the transition, resets <see cref="ProjectRuntime.RespawnRetries"/>,
///         keeps <see cref="ProjectRuntime.FlyVolumeId"/> AND
///         <see cref="ProjectRuntime.FlyMachineId"/> on the row (the
///         provisioner uses the stale machine id to force-destroy the dead
///         machine before booting a replacement on the same volume) and
///         raises <c>RuntimeStateChanged</c> with
///         <c>reason="user_restart"</c>, <c>triggeredBy="user:{userId}"</c>.</item>
///   <item>Persists with a single <c>SaveChangesAsync</c>; the
///         <c>DomainEventInterceptor</c> handles the audit row + downstream
///         handlers. The recurring <c>RuntimeProvisionerJob</c> picks the
///         Pending row up on its next 60s tick.</item>
///   <item>Projects the post-restart row + the five most-recent transitions
///         onto <see cref="RuntimeStatusResponse"/> — the same shape
///         <c>GET /api/projects/{projectId}/runtime/status</c> emits — so the
///         frontend can hydrate the runtime header without a follow-up GET.</item>
/// </list>
/// </summary>
public sealed class RestartRuntimeHandler
    : ICommandHandler<RestartRuntimeCommand, Result<RuntimeStatusResponse>>
{
    /// <summary>
    /// Sentinel prefix mapped to 404 by the controller. Used for missing
    /// runtime, soft-deleted runtime and the (future) non-member-caller
    /// existence-probe gate. Mirrors <c>GetProjectHandler.NotFoundPrefix</c>.
    /// </summary>
    public const string NotFoundPrefix = "not-found:";

    /// <summary>
    /// Sentinel prefix mapped to 409 by the controller — runtime exists but
    /// is in a state that disallows restart (anything other than Suspended,
    /// Failed, or Crashed). Separate from <see cref="NotFoundPrefix"/> because
    /// the caller should learn the current state without us redirecting them
    /// to a different existence gate.
    /// </summary>
    public const string ConflictPrefix = "conflict:";

    private readonly ApplicationDbContext _db;
    private readonly IMediator _mediator;
    private readonly FlyClient _fly;
    private readonly IBackgroundJobClient _backgroundJobs;
    private readonly ILogger<RestartRuntimeHandler> _logger;

    public RestartRuntimeHandler(
        ApplicationDbContext db,
        IMediator mediator,
        FlyClient fly,
        IBackgroundJobClient backgroundJobs,
        ILogger<RestartRuntimeHandler> logger)
    {
        _db = db;
        _mediator = mediator;
        _fly = fly;
        _backgroundJobs = backgroundJobs;
        _logger = logger;
    }

    public async Task<Result<RuntimeStatusResponse>> Handle(
        RestartRuntimeCommand request,
        CancellationToken cancellationToken)
    {
        // Most-recent (non-deleted) runtime for the branch. Soft-deleted rows
        // are filtered by the global query filter on ProjectRuntime — the
        // user experience is "no runtime to restart" exactly when the row
        // doesn't render in the status panel either.
        var runtime = await _db.ProjectRuntimes
            .Where(r => r.ProjectId == request.ProjectId && r.BranchId == request.BranchId)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (runtime is null)
        {
            return Result.Failure<RuntimeStatusResponse>(
                $"{NotFoundPrefix} No runtime exists for this branch.");
        }

        // Branch by state. Suspended → wake. Live / failed / stuck mid-boot →
        // hard reboot on the existing volume (stop the Fly machine first when
        // one is attached, then walk to Pending for the provisioner).
        switch (runtime.State)
        {
            case RuntimeState.Suspended:
                return await HandleSuspendedAsync(runtime, request, cancellationToken);

            case RuntimeState.Online:
            case RuntimeState.Failed:
            case RuntimeState.Crashed:
            case RuntimeState.Booting:
            case RuntimeState.Bootstrapping:
            case RuntimeState.Waking:
                await StopMachineBestEffortAsync(runtime, cancellationToken);
                break;

            case RuntimeState.Pending:
                return Result.Failure<RuntimeStatusResponse>(
                    $"{ConflictPrefix} Runtime is already restarting (Pending).");

            case RuntimeState.Suspending:
                return Result.Failure<RuntimeStatusResponse>(
                    $"{ConflictPrefix} Runtime is already stopping (Suspending).");

            default:
                return Result.Failure<RuntimeStatusResponse>(
                    $"{ConflictPrefix} Runtime is in state {runtime.State}; cannot restart.");
        }

        var restartResult = runtime.Restart(request.UserId);
        if (restartResult.IsFailure)
        {
            // Defence-in-depth: the pre-check above caught the only legitimate
            // failure mode. If Restart() rejected anyway, surface it as a 409
            // rather than a 500 — the entity is the source of truth on
            // transition legality.
            _logger.LogWarning(
                "RestartRuntime: entity-level rejection for runtime {RuntimeId} (user {UserId}): {Error}",
                runtime.Id, request.UserId, restartResult.Error);
            return Result.Failure<RuntimeStatusResponse>(
                $"{ConflictPrefix} {restartResult.Error}");
        }

        await _db.SaveChangesAsync(cancellationToken);

        // Kick the provisioner immediately for this newly-Pending runtime so
        // the user's restart click feels instant rather than waiting up to a
        // minute for the recurring sweep. The minutely sweep stays in place as
        // a safety net for the rare race where the row commits but this
        // enqueue doesn't (process killed between SaveChanges and Enqueue).
        _backgroundJobs.Enqueue<RuntimeProvisionerJob>(
            j => j.ProvisionOne(runtime.Id, JobCancellationToken.Null));

        _logger.LogInformation(
            "User {UserId} restarted runtime {RuntimeId} (project {ProjectId}, branch {BranchId}) — Pending, provisioner enqueued.",
            request.UserId, runtime.Id, request.ProjectId, request.BranchId);

        // Build the same RuntimeStatusResponse shape the GET status endpoint
        // emits so the frontend hydrates a single, consistent view model. We
        // re-read the five most recent transitions — the Restart() call just
        // appended one, and the projection is the same compound-index scan
        // RuntimeStatusController.GetStatus uses.
        var recentRows = await _db.RuntimeStateEvents
            .Where(e => e.RuntimeId == runtime.Id)
            .OrderByDescending(e => e.CreatedAt)
            .Take(5)
            .Select(e => new
            {
                e.FromState,
                e.ToState,
                e.Reason,
                e.TriggeredBy,
                e.Metadata,
                e.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        var recent = recentRows
            .Select(e => new RuntimeTransitionDto(
                e.FromState,
                e.ToState,
                e.Reason,
                e.TriggeredBy,
                e.CreatedAt))
            .ToList();

        // After a successful restart the runtime is Pending, never Failed,
        // so the ErrorReason / ErrorMessage block on RuntimeStatusResponse
        // stays null — same convention as RuntimeStatusController.GetStatus.
        return Result.Success(new RuntimeStatusResponse(
            runtime.Id,
            runtime.State,
            runtime.StateChangedAt,
            runtime.LastHeartbeatAt,
            runtime.FlyMachineId,
            runtime.ImageDigest,
            runtime.Region,
            recent));
    }

    /// <summary>
    /// Suspended branch of <see cref="Handle"/>. Delegates to
    /// <see cref="WakeRuntimeOnConnectCommand"/> (the same path the AgentHub
    /// used to walk on connect), then projects the post-wake row + recent
    /// transitions onto the same <see cref="RuntimeStatusResponse"/> shape the
    /// Failed/Crashed restart path emits — so the controller signature is
    /// unchanged and the frontend stays on a single hydration code path.
    ///
    /// <para>The wake command returns <c>NotApplicable</c> for non-Suspended
    /// states; we've already gated on Suspended at the call site, so any
    /// <c>NotApplicable</c> we see here is a race (someone else woke / failed
    /// the runtime between our state read and the wake dispatch) — we still
    /// return a 200 with the latest snapshot rather than treating it as an
    /// error, since the user's intent ("get this runtime running") is already
    /// satisfied.</para>
    /// </summary>
    private async Task<Result<RuntimeStatusResponse>> HandleSuspendedAsync(
        ProjectRuntime runtime,
        RestartRuntimeCommand request,
        CancellationToken cancellationToken)
    {
        var wake = await _mediator.Send(
            new WakeRuntimeOnConnectCommand(request.ProjectId, request.BranchId),
            cancellationToken);

        if (wake.IsFailure)
        {
            // The wake command only fails when the state machine refused the
            // Suspended → Waking transition — surface as a 409 (state-aware
            // error) the same way the Restart() entity-level rejection path
            // below does. Mirrors the existing defense-in-depth convention.
            _logger.LogWarning(
                "RestartRuntime (Suspended branch): wake command failed for runtime {RuntimeId} (user {UserId}): {Error}",
                runtime.Id, request.UserId, wake.Error);
            return Result.Failure<RuntimeStatusResponse>(
                $"{ConflictPrefix} {wake.Error}");
        }

        _logger.LogInformation(
            "User {UserId} restarted (wake) runtime {RuntimeId} (project {ProjectId}, branch {BranchId}) — Waking.",
            request.UserId, runtime.Id, request.ProjectId, request.BranchId);

        // Re-read the five most recent transitions — same shape and ordering
        // the Failed/Crashed path emits at the tail of Handle, so the
        // RuntimeStatusResponse hydration stays uniform across both branches.
        var recentRows = await _db.RuntimeStateEvents
            .Where(e => e.RuntimeId == runtime.Id)
            .OrderByDescending(e => e.CreatedAt)
            .Take(5)
            .Select(e => new
            {
                e.FromState,
                e.ToState,
                e.Reason,
                e.TriggeredBy,
                e.Metadata,
                e.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        var recent = recentRows
            .Select(e => new RuntimeTransitionDto(
                e.FromState,
                e.ToState,
                e.Reason,
                e.TriggeredBy,
                e.CreatedAt))
            .ToList();

        // The wake command mutated the in-memory runtime entity (same scoped
        // DbContext) so runtime.State is now Waking. No ErrorReason / Message —
        // the runtime is on its way to Online, not in a failed state.
        return Result.Success(new RuntimeStatusResponse(
            runtime.Id,
            runtime.State,
            runtime.StateChangedAt,
            runtime.LastHeartbeatAt,
            runtime.FlyMachineId,
            runtime.ImageDigest,
            runtime.Region,
            recent));
    }

    private async Task StopMachineBestEffortAsync(
        ProjectRuntime runtime,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(runtime.FlyMachineId))
        {
            return;
        }

        try
        {
            await _fly.StopMachineAsync(
                machineId: runtime.FlyMachineId,
                options: null,
                runtimeId: runtime.Id,
                ct: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "RestartRuntime: Fly StopMachine call failed for machine {MachineId} (runtime {RuntimeId}); provisioner will reconcile.",
                runtime.FlyMachineId, runtime.Id);
        }
    }
}

using MediatR;
using Microsoft.EntityFrameworkCore;
using Source.Features.FlyManagement;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimeLifecycle.Commands;

/// <summary>
/// User-driven wake trigger fired from <see cref="SignalR.Hubs.AgentHub"/>'s
/// <c>OnConnectedAsync</c> hook the first time a tab connects for a project
/// whose runtime has been suspended for cost. Encapsulates the three things
/// that have to happen together so the hub method stays a one-liner:
///
/// <list type="number">
///   <item>Find the most-recent (non-deleted) <see cref="ProjectRuntime"/> for
///         the given <see cref="ProjectId"/>. No row → silent
///         <see cref="WakeRuntimeOnConnectResult.NotApplicable"/>; the project
///         either never had a runtime or it was hard-deleted.</item>
///   <item>Only act when the runtime is <see cref="RuntimeState.Suspended"/>.
///         Online / Bootstrapping / Waking / Crashed / Deleted are all already
///         in their right states — the hub doesn't need to nudge them. Returns
///         <see cref="WakeRuntimeOnConnectResult.NotApplicable"/> with the
///         observed state for diagnostics.</item>
///   <item>Suspended runtime → call <see cref="ProjectRuntime.TransitionTo"/>
///         to <see cref="RuntimeState.Waking"/> with
///         <c>reason = "user:agent_hub_connect"</c> and
///         <c>triggeredBy = "user"</c>, then ask <see cref="FlyClient"/> to
///         start the underlying machine. Save first so the state row exists
///         before any Fly webhook can race back through the reconciler.</item>
/// </list>
///
/// <para><b>FlyMachineId may be null</b> on a fresh runtime that has never been
/// allocated a machine — that's a setup-bug case, not a wake case. We log +
/// return <see cref="WakeRuntimeOnConnectResult.NotApplicable"/> rather than
/// throwing; the user got "thinking…" instead of an error toast and the
/// operator sees the row in the warning channel.</para>
///
/// <para><b>Why no domain event for the wake notification.</b> The
/// <c>RuntimeStateChanged</c> event raised by <c>TransitionTo</c> already fans
/// out the Suspended → Waking edge to the project group. The hub layer pushes
/// the immediate <c>RuntimeWaking</c> affordance separately (it's a UX hint,
/// not a state record), so this command stays focused on the persistence + Fly
/// call and lets the hub do its own SignalR fan-out.</para>
/// </summary>
public record WakeRuntimeOnConnectCommand(Guid ProjectId, Guid BranchId)
    : ICommand<Result<WakeRuntimeOnConnectResult>>;

/// <summary>
/// Outcome of <see cref="WakeRuntimeOnConnectCommand"/>. <see cref="WokeRuntimeId"/>
/// is non-null only when we actually transitioned a runtime; the hub uses that to
/// decide whether to push <c>RuntimeWaking</c>.
/// </summary>
public record WakeRuntimeOnConnectResult(
    bool NotApplicable,
    Guid? WokeRuntimeId,
    Guid? RuntimeId,
    RuntimeState? ObservedState,
    string? Reason);

public class WakeRuntimeOnConnectCommandHandler
    : ICommandHandler<WakeRuntimeOnConnectCommand, Result<WakeRuntimeOnConnectResult>>
{
    private readonly ApplicationDbContext _db;
    private readonly FlyClient _fly;
    private readonly ILogger<WakeRuntimeOnConnectCommandHandler> _logger;

    public WakeRuntimeOnConnectCommandHandler(
        ApplicationDbContext db,
        FlyClient fly,
        ILogger<WakeRuntimeOnConnectCommandHandler> logger)
    {
        _db = db;
        _fly = fly;
        _logger = logger;
    }

    public async Task<Result<WakeRuntimeOnConnectResult>> Handle(
        WakeRuntimeOnConnectCommand request,
        CancellationToken cancellationToken)
    {
        // Most-recent non-deleted runtime for this project+branch. The query
        // filter on ProjectRuntime hides soft-deleted rows by default —
        // Deleted-state runtimes are within their 30-day window and shouldn't
        // be woken. Filtering by BranchId (not ProjectId alone) because a
        // project owns one ProjectRuntime per branch after CopyBranch — a
        // project-only filter would arbitrarily wake a sibling branch's
        // runtime and leave the actually-connecting branch still Suspended.
        var runtime = await _db.ProjectRuntimes
            .Where(r => r.ProjectId == request.ProjectId && r.BranchId == request.BranchId)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (runtime is null)
        {
            return Result.Success(new WakeRuntimeOnConnectResult(
                NotApplicable: true,
                WokeRuntimeId: null,
                RuntimeId: null,
                ObservedState: null,
                Reason: "no_runtime_for_project"));
        }

        if (runtime.State != RuntimeState.Suspended)
        {
            return Result.Success(new WakeRuntimeOnConnectResult(
                NotApplicable: true,
                WokeRuntimeId: null,
                RuntimeId: runtime.Id,
                ObservedState: runtime.State,
                Reason: "not_suspended"));
        }

        if (string.IsNullOrEmpty(runtime.FlyMachineId))
        {
            // Setup bug — a Suspended runtime with no machine id can't be
            // started. Don't fail the connection; just log and skip.
            _logger.LogWarning(
                "WakeRuntimeOnConnect: runtime {RuntimeId} for project {ProjectId} is Suspended but has no FlyMachineId; cannot wake.",
                runtime.Id, request.ProjectId);
            return Result.Success(new WakeRuntimeOnConnectResult(
                NotApplicable: true,
                WokeRuntimeId: null,
                RuntimeId: runtime.Id,
                ObservedState: runtime.State,
                Reason: "missing_fly_machine_id"));
        }

        var transition = runtime.TransitionTo(
            RuntimeState.Waking,
            reason: "user:agent_hub_connect",
            triggeredBy: "user");
        if (!transition.IsSuccess)
        {
            // State machine refused — the only path to here that's legal is
            // Suspended → Waking, and the guard above checks that, so a
            // failure means the state machine has been edited out from under
            // us. Surface, don't crash the connection.
            _logger.LogWarning(
                "WakeRuntimeOnConnect: state-machine refused Suspended -> Waking for runtime {RuntimeId}: {Error}",
                runtime.Id, transition.Error);
            return Result.Failure<WakeRuntimeOnConnectResult>(transition.Error!);
        }

        await _db.SaveChangesAsync(cancellationToken);

        // Fire the Fly call after persistence so a Fly webhook racing back
        // through the reconciler observes the Waking row instead of the stale
        // Suspended one. FlyClient swallows transport errors into FlyOperation
        // audit rows — a transient blip won't take the connection down.
        try
        {
            await _fly.StartMachineAsync(
                machineId: runtime.FlyMachineId,
                runtimeId: runtime.Id,
                ct: cancellationToken);
        }
        catch (Exception ex)
        {
            // Already logged by FlyClient; we let the connection succeed and
            // leave reconciliation to the next reconciler pass.
            _logger.LogWarning(ex,
                "WakeRuntimeOnConnect: Fly StartMachine call failed for machine {MachineId} (runtime {RuntimeId}); reconciler will retry.",
                runtime.FlyMachineId, runtime.Id);
        }

        _logger.LogInformation(
            "WakeRuntimeOnConnect: runtime {RuntimeId} transitioned Suspended -> Waking for project {ProjectId}.",
            runtime.Id, request.ProjectId);

        return Result.Success(new WakeRuntimeOnConnectResult(
            NotApplicable: false,
            WokeRuntimeId: runtime.Id,
            RuntimeId: runtime.Id,
            ObservedState: RuntimeState.Waking,
            Reason: null));
    }
}

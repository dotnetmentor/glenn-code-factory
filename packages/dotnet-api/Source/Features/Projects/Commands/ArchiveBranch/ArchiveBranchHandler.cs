using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Source.Features.Conversations.Models;
using Source.Features.FlyManagement;
using Source.Features.Projects.Models;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Commands.ArchiveBranch;

/// <summary>
/// Handles <see cref="ArchiveBranchCommand"/>. Single transaction: load the
/// branch (+ its active runtime), refuse on default-branch and in-flight
/// session, flip the archive flag, optionally walk the runtime to
/// <c>Suspending</c>, save.
///
/// <para><b>Error shape.</b> Failures return stable string codes via
/// <c>Result.Error</c> — <see cref="NotFoundError"/> / <see cref="IsDefaultError"/>
/// / <see cref="HasRunningSessionError"/>. The controller maps each to its
/// HTTP status and pairs it with a human-readable message from a static
/// lookup, so the wire shape stays <c>{ error, message }</c> across both
/// archive endpoints.</para>
/// </summary>
public sealed class ArchiveBranchHandler : ICommandHandler<ArchiveBranchCommand, Result>
{
    /// <summary>Stable error code the controller maps to HTTP 404. Branch not on this project, or branch missing.</summary>
    public const string NotFoundError = "not_found";

    /// <summary>Stable error code the controller maps to HTTP 400 when the caller targets the project's default branch.</summary>
    public const string IsDefaultError = "is_default";

    /// <summary>Stable error code the controller maps to HTTP 400 when a turn is in flight on the branch.</summary>
    public const string HasRunningSessionError = "has_running_session";

    private readonly ApplicationDbContext _db;
    private readonly FlyClient _fly;
    private readonly ILogger<ArchiveBranchHandler> _logger;

    public ArchiveBranchHandler(
        ApplicationDbContext db,
        FlyClient fly,
        ILogger<ArchiveBranchHandler> logger)
    {
        _db = db;
        _fly = fly;
        _logger = logger;
    }

    public async Task<Result> Handle(ArchiveBranchCommand request, CancellationToken cancellationToken)
    {
        // -------- 1. Load tracked — Archive() mutates state we save back --------
        var branch = await _db.ProjectBranches
            .FirstOrDefaultAsync(
                b => b.Id == request.BranchId && b.ProjectId == request.ProjectId,
                cancellationToken);

        if (branch is null)
        {
            return Result.Failure(NotFoundError);
        }

        // -------- 2. Refuse: default branch --------
        // Belt-and-suspenders — the entity also throws if Archive() is called
        // on a default branch, but a clean Result.Failure here lets the
        // controller surface the friendly 400 without an exception.
        if (branch.IsDefault)
        {
            return Result.Failure(IsDefaultError);
        }

        // -------- 3. Idempotent: already archived --------
        // Short-circuit BEFORE the running-session check so a re-archive call
        // doesn't surprise the user with a "stop your turn first" error on a
        // branch that's already archived.
        if (branch.IsArchived)
        {
            return Result.Success();
        }

        // -------- 4. Refuse: in-flight session --------
        // Any AgentSession on this branch's conversations that's Pending or
        // Running blocks the archive — suspending the runtime mid-turn would
        // lose work.
        var hasRunningSession = await _db.AgentSessions
            .AnyAsync(
                s => s.Conversation.BranchId == branch.Id
                  && (s.Status == AgentSessionStatus.Pending
                   || s.Status == AgentSessionStatus.Running),
                cancellationToken);

        if (hasRunningSession)
        {
            return Result.Failure(HasRunningSessionError);
        }

        // -------- 5. Archive on the entity (raises ProjectBranchArchived) --------
        branch.Archive();

        // -------- 6. Suspend the active runtime if it's running-ish --------
        // "Active" = most recent non-Deleted ProjectRuntime for this branch.
        // The global !IsDeleted query filter on ProjectRuntime already filters
        // tombstoned rows, so OrderByDescending(CreatedAt).FirstOrDefault is
        // safe to use as "live runtime".
        //
        // The state machine today only permits Online → Suspending directly
        // (Booting/Bootstrapping/Waking each need to land in Online or Crashed
        // first). We still TRY the transition for the broader running-ish set
        // and let TransitionTo() validate — when it refuses, we log a warning
        // and let the archive commit anyway. Once the runtime reaches Online
        // (or Crashed) the IdlerJob (or operator) will reconcile; archived
        // branches have no conversation activity so idle-suspend fires
        // promptly.
        var activeRuntime = await _db.ProjectRuntimes
            .Where(r => r.BranchId == branch.Id)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (activeRuntime is not null && IsRunningIshState(activeRuntime.State))
        {
            var metadata = JsonSerializer.Serialize(new
            {
                triggeredByJob = "branch-archive",
                branchId = branch.Id,
                priorState = activeRuntime.State.ToString(),
            });

            var transition = activeRuntime.TransitionTo(
                RuntimeState.Suspending,
                reason: "BranchArchived",
                triggeredBy: "user",
                metadata: metadata);

            if (!transition.IsSuccess)
            {
                _logger.LogWarning(
                    "ArchiveBranch: runtime {RuntimeId} could not transition {State} -> Suspending now (will be reconciled later): {Error}",
                    activeRuntime.Id, activeRuntime.State, transition.Error);
            }
            else
            {
                // Best-effort Fly StopMachine call. Without this, the DB flips to
                // Suspending but the Fly machine keeps running indefinitely —
                // exactly the drift scenario the audit found (7 runtimes stuck
                // Suspending, oldest 19h old, all from archived branches).
                // Mirrors IdlerJob.SuspendOne: log + swallow transport errors so
                // the archive still commits; the RuntimeReconcilerJob retries the
                // StopMachine call for any (started, Suspending) drift it sees.
                if (!string.IsNullOrEmpty(activeRuntime.FlyMachineId))
                {
                    try
                    {
                        await _fly.StopMachineAsync(
                            machineId: activeRuntime.FlyMachineId,
                            options: null,
                            runtimeId: activeRuntime.Id,
                            ct: cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "ArchiveBranch: Fly StopMachine call failed for machine {MachineId} (runtime {RuntimeId}); reconciler will retry.",
                            activeRuntime.FlyMachineId, activeRuntime.Id);
                    }
                }
                else
                {
                    _logger.LogWarning(
                        "ArchiveBranch: runtime {RuntimeId} has no FlyMachineId; transitioned to Suspending but no Fly call issued.",
                        activeRuntime.Id);
                }
            }
        }

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "ArchiveBranch: branch {BranchId} on project {ProjectId} archived.",
            branch.Id, branch.ProjectId);

        return Result.Success();
    }

    /// <summary>
    /// True for runtime states whose Fly machine is live or starting up — the
    /// set we want to push toward Suspending on archive. Other states (Pending,
    /// Suspending, Suspended, Crashed, Failed, Deleting, Deleted) already
    /// encode "not running" or "operator must intervene"; leave them alone.
    /// </summary>
    private static bool IsRunningIshState(RuntimeState s) =>
        s == RuntimeState.Online
        || s == RuntimeState.Booting
        || s == RuntimeState.Bootstrapping
        || s == RuntimeState.Waking;
}

using MediatR;
using Microsoft.EntityFrameworkCore;
using Source.Features.FlyManagement;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimeLifecycle.Commands.ForceStopRuntime;

/// <summary>
/// Handler for <see cref="ForceStopRuntimeCommand"/>. Accepts the same source
/// states as <see cref="Controllers.RuntimeAdminController.ForceStop"/> and
/// walks the runtime to <see cref="RuntimeState.Suspending"/> with a best-effort
/// Fly <c>StopMachine</c> call.
/// </summary>
public sealed class ForceStopRuntimeHandler
    : ICommandHandler<ForceStopRuntimeCommand, Result<RuntimeStatusResponse>>
{
    public const string NotFoundPrefix = "not-found:";
    public const string ConflictPrefix = "conflict:";

    private static readonly HashSet<RuntimeState> ForceStoppableStates = new()
    {
        RuntimeState.Online,
        RuntimeState.Booting,
        RuntimeState.Bootstrapping,
        RuntimeState.Waking,
    };

    private readonly ApplicationDbContext _db;
    private readonly FlyClient _fly;
    private readonly ILogger<ForceStopRuntimeHandler> _logger;

    public ForceStopRuntimeHandler(
        ApplicationDbContext db,
        FlyClient fly,
        ILogger<ForceStopRuntimeHandler> logger)
    {
        _db = db;
        _fly = fly;
        _logger = logger;
    }

    public async Task<Result<RuntimeStatusResponse>> Handle(
        ForceStopRuntimeCommand request,
        CancellationToken cancellationToken)
    {
        var runtime = await _db.ProjectRuntimes
            .Where(r => r.ProjectId == request.ProjectId && r.BranchId == request.BranchId)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (runtime is null)
        {
            return Result.Failure<RuntimeStatusResponse>(
                $"{NotFoundPrefix} No runtime exists for this branch.");
        }

        if (!ForceStoppableStates.Contains(runtime.State))
        {
            return Result.Failure<RuntimeStatusResponse>(
                $"{ConflictPrefix} Runtime is in state {runtime.State}; cannot force-stop.");
        }

        var transition = runtime.TransitionTo(
            RuntimeState.Suspending,
            "user_force_stop",
            $"user:{request.UserId}");

        if (transition.IsFailure)
        {
            _logger.LogWarning(
                "ForceStopRuntime: entity-level rejection for runtime {RuntimeId} (user {UserId}): {Error}",
                runtime.Id, request.UserId, transition.Error);
            return Result.Failure<RuntimeStatusResponse>(
                $"{ConflictPrefix} {transition.Error}");
        }

        await _db.SaveChangesAsync(cancellationToken);

        if (!string.IsNullOrEmpty(runtime.FlyMachineId))
        {
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
                    "ForceStopRuntime: Fly StopMachine call failed for machine {MachineId} (runtime {RuntimeId}); reconciler will retry.",
                    runtime.FlyMachineId, runtime.Id);
            }
        }

        _logger.LogInformation(
            "User {UserId} force-stopped runtime {RuntimeId} (project {ProjectId}, branch {BranchId}) — Suspending.",
            request.UserId, runtime.Id, request.ProjectId, request.BranchId);

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
}

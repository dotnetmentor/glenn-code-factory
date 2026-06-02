using MediatR;
using Microsoft.EntityFrameworkCore;
using Source.Features.FlyManagement;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimeLifecycle.Commands.SuspendRuntime;

/// <summary>
/// Handler for <see cref="SuspendRuntimeCommand"/>. Mirrors the operator
/// <c>force-suspend</c> path on <see cref="Controllers.RuntimeAdminController"/>
/// but is scoped to project owners and only accepts
/// <see cref="RuntimeState.Online"/> → <see cref="RuntimeState.Suspending"/>.
/// </summary>
public sealed class SuspendRuntimeHandler
    : ICommandHandler<SuspendRuntimeCommand, Result<RuntimeStatusResponse>>
{
    public const string NotFoundPrefix = "not-found:";
    public const string ConflictPrefix = "conflict:";

    private readonly ApplicationDbContext _db;
    private readonly FlyClient _fly;
    private readonly ILogger<SuspendRuntimeHandler> _logger;

    public SuspendRuntimeHandler(
        ApplicationDbContext db,
        FlyClient fly,
        ILogger<SuspendRuntimeHandler> logger)
    {
        _db = db;
        _fly = fly;
        _logger = logger;
    }

    public async Task<Result<RuntimeStatusResponse>> Handle(
        SuspendRuntimeCommand request,
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

        if (runtime.State != RuntimeState.Online)
        {
            return Result.Failure<RuntimeStatusResponse>(
                $"{ConflictPrefix} Runtime is in state {runtime.State}; can only suspend from Online.");
        }

        var transition = runtime.TransitionTo(
            RuntimeState.Suspending,
            "user_suspend",
            $"user:{request.UserId}");

        if (transition.IsFailure)
        {
            _logger.LogWarning(
                "SuspendRuntime: entity-level rejection for runtime {RuntimeId} (user {UserId}): {Error}",
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
                    "SuspendRuntime: Fly StopMachine call failed for machine {MachineId} (runtime {RuntimeId}); reconciler will retry.",
                    runtime.FlyMachineId, runtime.Id);
            }
        }

        _logger.LogInformation(
            "User {UserId} suspended runtime {RuntimeId} (project {ProjectId}, branch {BranchId}) — Suspending.",
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

using Hangfire;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Source.Features.FlyManagement;
using Source.Features.RuntimeLifecycle.Jobs;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimeLifecycle.Commands.ResetRuntimeFromScratch;

public sealed class ResetRuntimeFromScratchHandler
    : ICommandHandler<ResetRuntimeFromScratchCommand, Result<RuntimeStatusResponse>>
{
    public const string NotFoundPrefix = "not-found:";
    public const string ConflictPrefix = "conflict:";

    private readonly ApplicationDbContext _db;
    private readonly FlyClient _fly;
    private readonly IBackgroundJobClient _backgroundJobs;
    private readonly ILogger<ResetRuntimeFromScratchHandler> _logger;

    public ResetRuntimeFromScratchHandler(
        ApplicationDbContext db,
        FlyClient fly,
        IBackgroundJobClient backgroundJobs,
        ILogger<ResetRuntimeFromScratchHandler> logger)
    {
        _db = db;
        _fly = fly;
        _backgroundJobs = backgroundJobs;
        _logger = logger;
    }

    public async Task<Result<RuntimeStatusResponse>> Handle(
        ResetRuntimeFromScratchCommand request,
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

        var machineId = runtime.FlyMachineId;
        var volumeId = runtime.FlyVolumeId;

        await DestroyMachineBestEffortAsync(runtime, machineId, cancellationToken);
        await DestroyVolumeBestEffortAsync(runtime, volumeId, cancellationToken);

        var resetResult = runtime.ResetFromScratch(request.UserId);
        if (resetResult.IsFailure)
        {
            _logger.LogWarning(
                "ResetRuntimeFromScratch: entity-level rejection for runtime {RuntimeId} (user {UserId}): {Error}",
                runtime.Id, request.UserId, resetResult.Error);
            return Result.Failure<RuntimeStatusResponse>(
                $"{ConflictPrefix} {resetResult.Error}");
        }

        await _db.SaveChangesAsync(cancellationToken);

        _backgroundJobs.Enqueue<RuntimeProvisionerJob>(
            j => j.ProvisionOne(runtime.Id, JobCancellationToken.Null));

        _logger.LogInformation(
            "User {UserId} reset-from-scratch runtime {RuntimeId} (project {ProjectId}, branch {BranchId}) — Pending, provisioner enqueued.",
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

    private async Task DestroyMachineBestEffortAsync(
        ProjectRuntime runtime,
        string? machineId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(machineId))
        {
            return;
        }

        try
        {
            await _fly.DestroyMachineAsync(
                machineId,
                force: true,
                runtimeId: runtime.Id,
                ct: cancellationToken);
        }
        catch (FlyApiException ex) when (ex.StatusCode == 404)
        {
            _logger.LogInformation(
                "ResetRuntimeFromScratch: machine {MachineId} already gone (404) for runtime {RuntimeId}.",
                machineId, runtime.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ResetRuntimeFromScratch: Fly DestroyMachine failed for {MachineId} (runtime {RuntimeId}); continuing with volume wipe.",
                machineId, runtime.Id);
        }
    }

    private async Task DestroyVolumeBestEffortAsync(
        ProjectRuntime runtime,
        string? volumeId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(volumeId))
        {
            return;
        }

        try
        {
            await _fly.DestroyVolumeAsync(volumeId, runtimeId: runtime.Id, ct: cancellationToken);
        }
        catch (FlyApiException ex) when (ex.StatusCode == 404)
        {
            _logger.LogInformation(
                "ResetRuntimeFromScratch: volume {VolumeId} already gone (404) for runtime {RuntimeId}.",
                volumeId, runtime.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ResetRuntimeFromScratch: Fly DestroyVolume failed for {VolumeId} (runtime {RuntimeId}); clearing DB refs anyway.",
                volumeId, runtime.Id);
        }
    }
}

using Hangfire;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Source.Features.BoxManagement;
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
    private readonly BoxClient _box;
    private readonly IBackgroundJobClient _backgroundJobs;
    private readonly ILogger<ResetRuntimeFromScratchHandler> _logger;

    public ResetRuntimeFromScratchHandler(
        ApplicationDbContext db,
        BoxClient box,
        IBackgroundJobClient backgroundJobs,
        ILogger<ResetRuntimeFromScratchHandler> logger)
    {
        _db = db;
        _box = box;
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

        var boxId = runtime.BoxId;

        await DeleteBoxBestEffortAsync(runtime, boxId, cancellationToken);

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
            runtime.BoxId,
            runtime.TemplateBoxId,
            runtime.Region,
            recent));
    }

    /// <summary>
    /// Permanently delete the abandoned box (and its snapshots) so a reset never
    /// leaves billable state behind. Best-effort: 404 means "already gone", any
    /// other failure is logged and the DB refs are cleared anyway — the TTL
    /// guardrail archives an orphaned box and Box Cleanup can remove it later.
    /// </summary>
    private async Task DeleteBoxBestEffortAsync(
        ProjectRuntime runtime,
        string? boxId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(boxId))
        {
            return;
        }

        try
        {
            await _box.DeleteBoxAsync(boxId, runtimeId: runtime.Id, ct: cancellationToken);
        }
        catch (BoxApiException ex) when (ex.StatusCode == 404)
        {
            _logger.LogInformation(
                "ResetRuntimeFromScratch: box {BoxId} already gone (404) for runtime {RuntimeId}.",
                boxId, runtime.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ResetRuntimeFromScratch: Box delete failed for {BoxId} (runtime {RuntimeId}); clearing DB refs anyway.",
                boxId, runtime.Id);
        }
    }
}

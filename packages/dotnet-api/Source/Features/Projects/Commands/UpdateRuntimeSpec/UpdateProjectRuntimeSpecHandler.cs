using Hangfire;
using Microsoft.EntityFrameworkCore;
using Source.Features.Projects.Models;
using Source.Features.RuntimeLifecycle.Jobs;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Commands.UpdateRuntimeSpec;

public sealed class UpdateProjectRuntimeSpecHandler
    : ICommandHandler<UpdateProjectRuntimeSpecCommand, Result<UpdateProjectRuntimeSpecResponse>>
{
    private readonly ApplicationDbContext _db;
    private readonly IBackgroundJobClient _backgroundJobs;
    private readonly ILogger<UpdateProjectRuntimeSpecHandler> _logger;

    public UpdateProjectRuntimeSpecHandler(
        ApplicationDbContext db,
        IBackgroundJobClient backgroundJobs,
        ILogger<UpdateProjectRuntimeSpecHandler> logger)
    {
        _db = db;
        _backgroundJobs = backgroundJobs;
        _logger = logger;
    }

    public async Task<Result<UpdateProjectRuntimeSpecResponse>> Handle(
        UpdateProjectRuntimeSpecCommand request,
        CancellationToken cancellationToken)
    {
        var project = await _db.Projects
            .FirstOrDefaultAsync(p => p.Id == request.ProjectId, cancellationToken);

        if (project is null)
        {
            return Result.Failure<UpdateProjectRuntimeSpecResponse>("Project not found");
        }

        var setResult = project.SetRuntimeSpec(
            cpuKind: request.CpuKind,
            cpus: request.Cpus,
            memoryMb: request.MemoryMb,
            volumeSizeGb: request.VolumeSizeGb);

        if (setResult.IsFailure)
        {
            return Result.Failure<UpdateProjectRuntimeSpecResponse>(setResult.Error!);
        }

        var restartedBranchNames = new List<string>();
        string? volumeSizeNote = null;

        if (request.ApplyToExistingBranches)
        {
            var runtimes = await _db.ProjectRuntimes
                .Where(r => r.ProjectId == request.ProjectId)
                .Join(
                    _db.ProjectBranches.Where(b => !b.IsArchived),
                    r => r.BranchId,
                    b => b.Id,
                    (r, b) => new { Runtime = r, b.Name })
                .OrderByDescending(x => x.Runtime.CreatedAt)
                .ToListAsync(cancellationToken);

            var seenBranches = new HashSet<Guid>();
            var reprovisionedRuntimeIds = new List<Guid>();
            var anyVolumeSizeDrift = false;

            foreach (var row in runtimes)
            {
                var runtime = row.Runtime;
                if (!seenBranches.Add(runtime.BranchId))
                {
                    continue;
                }

                if (runtime.HardwareSpecMatches(
                        request.CpuKind,
                        request.Cpus,
                        request.MemoryMb,
                        request.VolumeSizeGb))
                {
                    continue;
                }

                if (runtime.VolumeSizeGb != request.VolumeSizeGb)
                {
                    anyVolumeSizeDrift = true;
                }

                var reprovision = runtime.ReprovisionAfterSpecChange(
                    request.CpuKind,
                    request.Cpus,
                    request.MemoryMb,
                    request.VolumeSizeGb,
                    request.UserId);

                if (reprovision.IsFailure)
                {
                    _logger.LogWarning(
                        "Apply runtime spec: skipped runtime {RuntimeId} on branch {BranchName}: {Error}",
                        runtime.Id, row.Name, reprovision.Error);
                    continue;
                }

                restartedBranchNames.Add(row.Name);
                reprovisionedRuntimeIds.Add(runtime.Id);
            }

            if (anyVolumeSizeDrift)
            {
                volumeSizeNote =
                    "Disk size on existing branches is unchanged on the live Fly volume until reset from scratch; CPU and RAM will update after restart.";
            }

            await _db.SaveChangesAsync(cancellationToken);

            foreach (var runtimeId in reprovisionedRuntimeIds)
            {
                _backgroundJobs.Enqueue<RuntimeProvisionerJob>(
                    j => j.ProvisionOne(runtimeId, JobCancellationToken.Null));
            }

            if (restartedBranchNames.Count > 0)
            {
                _logger.LogInformation(
                    "Applied runtime spec to {Count} existing branch(es) on project {ProjectId}: {Branches}",
                    restartedBranchNames.Count,
                    request.ProjectId,
                    string.Join(", ", restartedBranchNames));
            }
        }
        else
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        _logger.LogInformation(
            "Updated runtime spec for project {ProjectId}: cpuKind={CpuKind} cpus={Cpus} memoryMb={MemoryMb} volumeSizeGb={VolumeSizeGb} applyExisting={ApplyExisting}",
            request.ProjectId,
            project.RuntimeCpuKind,
            project.RuntimeCpus,
            project.RuntimeMemoryMb,
            project.RuntimeVolumeSizeGb,
            request.ApplyToExistingBranches);

        return Result.Success(new UpdateProjectRuntimeSpecResponse(
            ProjectId: project.Id,
            CpuKind: project.RuntimeCpuKind,
            Cpus: project.RuntimeCpus,
            MemoryMb: project.RuntimeMemoryMb,
            VolumeSizeGb: project.RuntimeVolumeSizeGb,
            AppliedToExistingBranchCount: restartedBranchNames.Count,
            RestartedBranchNames: restartedBranchNames,
            VolumeSizeNote: volumeSizeNote));
    }
}

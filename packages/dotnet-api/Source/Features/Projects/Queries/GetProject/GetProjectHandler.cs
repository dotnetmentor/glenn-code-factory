using Microsoft.EntityFrameworkCore;
using Source.Features.Projects.Models;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Queries.GetProject;

public sealed class GetProjectHandler : IQueryHandler<GetProjectQuery, Result<ProjectDto>>
{
    public const string NotFoundPrefix = "not-found:";

    private readonly ApplicationDbContext _db;

    public GetProjectHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<ProjectDto>> Handle(GetProjectQuery request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CallerUserId))
        {
            return Result.Failure<ProjectDto>($"{NotFoundPrefix} unauthenticated");
        }

        var project = await _db.Projects
            .AsNoTracking()
            .Where(p => p.Id == request.ProjectId)
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.WorkspaceId,
                p.GithubRepoOwner,
                p.GithubRepoName,
                p.GithubInstallationId,
                p.PreviewPort,
                p.RuntimeCpuKind,
                p.RuntimeCpus,
                p.RuntimeMemoryMb,
                p.RuntimeVolumeSizeGb,
                p.ModelId,
                ModelSlug = p.Model != null ? p.Model.Slug : null,
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (project is null)
        {
            return Result.Failure<ProjectDto>($"{NotFoundPrefix} project {request.ProjectId} not found");
        }

        var isMember = await _db.WorkspaceMemberships
            .AsNoTracking()
            .AnyAsync(
                m => m.WorkspaceId == project.WorkspaceId && m.UserId == request.CallerUserId,
                cancellationToken);

        if (!isMember)
        {
            return Result.Failure<ProjectDto>($"{NotFoundPrefix} project {request.ProjectId} not found");
        }

        var defaultBranch = await _db.ProjectBranches
            .AsNoTracking()
            .Where(b => b.ProjectId == project.Id && b.IsDefault)
            .Select(b => new { b.Id, b.Name })
            .SingleOrDefaultAsync(cancellationToken);

        if (defaultBranch is null)
        {
            return Result.Failure<ProjectDto>($"{NotFoundPrefix} project {request.ProjectId} has no default branch");
        }

        var runtime = await _db.ProjectRuntimes
            .AsNoTracking()
            .Where(r => r.ProjectId == project.Id && r.BranchId == defaultBranch.Id)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new { r.Id, r.State })
            .FirstOrDefaultAsync(cancellationToken);

        return Result.Success(new ProjectDto(
            Id: project.Id,
            Name: project.Name,
            WorkspaceId: project.WorkspaceId,
            DefaultBranchId: defaultBranch.Id,
            DefaultBranchName: defaultBranch.Name,
            RuntimeId: runtime?.Id ?? Guid.Empty,
            RuntimeState: runtime?.State ?? RuntimeState.Pending,
            GithubRepoOwner: project.GithubRepoOwner,
            GithubRepoName: project.GithubRepoName,
            GithubInstallationId: project.GithubInstallationId,
            PreviewPort: project.PreviewPort,
            RuntimeCpuKind: project.RuntimeCpuKind,
            RuntimeCpus: project.RuntimeCpus,
            RuntimeMemoryMb: project.RuntimeMemoryMb,
            RuntimeVolumeSizeGb: project.RuntimeVolumeSizeGb,
            ModelId: project.ModelId,
            ModelSlug: project.ModelSlug));
    }
}

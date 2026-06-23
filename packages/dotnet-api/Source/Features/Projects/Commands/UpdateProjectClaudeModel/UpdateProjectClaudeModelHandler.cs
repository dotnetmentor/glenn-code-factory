using Microsoft.EntityFrameworkCore;
using Source.Features.Projects.Models;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Commands.UpdateProjectClaudeModel;

/// <summary>
/// Handler for <see cref="UpdateProjectClaudeModelCommand"/>. Mirrors
/// <c>UpdateProjectCursorModelHandler</c>: validate the model id against the
/// active <c>ClaudeModels</c> catalog, persist via
/// <c>Project.SetClaudeModel(...)</c>, then return the refreshed
/// <see cref="ProjectDto"/> (carrying both the Cursor and Claude defaults so
/// the frontend can hydrate either picker).
/// </summary>
public sealed class UpdateProjectClaudeModelHandler
    : ICommandHandler<UpdateProjectClaudeModelCommand, Result<ProjectDto>>
{
    public const string NotFoundPrefix = "not-found:";
    public const string InvalidModelError = "invalid_model";

    private readonly ApplicationDbContext _db;
    private readonly ILogger<UpdateProjectClaudeModelHandler> _logger;

    public UpdateProjectClaudeModelHandler(
        ApplicationDbContext db,
        ILogger<UpdateProjectClaudeModelHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Result<ProjectDto>> Handle(
        UpdateProjectClaudeModelCommand request,
        CancellationToken cancellationToken)
    {
        var project = await _db.Projects
            .FirstOrDefaultAsync(p => p.Id == request.ProjectId, cancellationToken);

        if (project is null)
        {
            return Result.Failure<ProjectDto>($"{NotFoundPrefix} project {request.ProjectId} not found");
        }

        if (request.ModelId is { } modelId)
        {
            var modelOk = await _db.ClaudeModels
                .AsNoTracking()
                .AnyAsync(m => m.Id == modelId && m.IsActive, cancellationToken);
            if (!modelOk)
            {
                return Result.Failure<ProjectDto>(InvalidModelError);
            }
        }

        project.SetClaudeModel(request.ModelId);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "UpdateProjectClaudeModel: project {ProjectId} default Claude model set to {ModelId}.",
            project.Id, project.ClaudeModelId);

        var defaultBranch = await _db.ProjectBranches
            .AsNoTracking()
            .Where(b => b.ProjectId == project.Id && b.IsDefault)
            .Select(b => new { b.Id, b.Name })
            .SingleOrDefaultAsync(cancellationToken);

        var runtime = await _db.ProjectRuntimes
            .AsNoTracking()
            .Where(r => r.ProjectId == project.Id && (defaultBranch == null || r.BranchId == defaultBranch.Id))
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new { r.Id, r.State })
            .FirstOrDefaultAsync(cancellationToken);

        string? cursorSlug = null;
        if (project.ModelId is { } cursorId)
        {
            cursorSlug = await _db.CursorModels
                .AsNoTracking()
                .Where(m => m.Id == cursorId)
                .Select(m => m.Slug)
                .FirstOrDefaultAsync(cancellationToken);
        }

        string? claudeSlug = null;
        if (project.ClaudeModelId is { } claudeId)
        {
            claudeSlug = await _db.ClaudeModels
                .AsNoTracking()
                .Where(m => m.Id == claudeId)
                .Select(m => m.Slug)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return Result.Success(new ProjectDto(
            Id: project.Id,
            Name: project.Name,
            WorkspaceId: project.WorkspaceId,
            DefaultBranchId: defaultBranch?.Id ?? Guid.Empty,
            DefaultBranchName: defaultBranch?.Name ?? string.Empty,
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
            ModelSlug: cursorSlug,
            ClaudeModelId: project.ClaudeModelId,
            ClaudeModelSlug: claudeSlug));
    }
}

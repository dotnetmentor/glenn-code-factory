using Microsoft.EntityFrameworkCore;
using Source.Features.Projects.AgentPermissions.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.AgentPermissions.Queries.GetProjectAgentPermissions;

/// <summary>
/// Handler for <see cref="GetProjectAgentPermissionsQuery"/>. Loads the project's
/// override row (if any) after verifying the caller is a member of the project's
/// workspace.
///
/// <para><b>Visibility gate.</b> Same shape as
/// <c>GetProjectHandler</c>: missing project, soft-deleted project and
/// non-member caller all collapse to a <c>not-found:</c> failure so the
/// controller can return 404 without leaking project existence. The override
/// row itself is not soft-deletable, so this handler doesn't worry about
/// tombstoned rows on that side.</para>
///
/// <para><b>Why <see cref="ProjectAgentPermissionsDto"/>? not the entity?</b>
/// The wire DTO has a deliberate field order and excludes audit columns
/// (CreatedAt/UpdatedAt). Projecting in the handler keeps the controller a
/// thin pass-through and avoids accidentally exposing internal state via
/// Swagger.</para>
/// </summary>
public sealed class GetProjectAgentPermissionsHandler
    : IQueryHandler<GetProjectAgentPermissionsQuery, Result<ProjectAgentPermissionsDto?>>
{
    /// <summary>
    /// Sentinel prefix the controller matches on to map "no project / no
    /// access" to 404 instead of 400. Mirrors
    /// <c>GetProjectHandler.NotFoundPrefix</c>.
    /// </summary>
    public const string NotFoundPrefix = "not-found:";

    private readonly ApplicationDbContext _db;

    public GetProjectAgentPermissionsHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<ProjectAgentPermissionsDto?>> Handle(
        GetProjectAgentPermissionsQuery request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CallerUserId))
        {
            return Result.Failure<ProjectAgentPermissionsDto?>(
                $"{NotFoundPrefix} unauthenticated");
        }

        // Step 1: existence + visibility gate. The global !IsDeleted query
        // filter on Project means a tombstoned project shows up as "not
        // found" here.
        var project = await _db.Projects
            .AsNoTracking()
            .Where(p => p.Id == request.ProjectId)
            .Select(p => new { p.Id, p.WorkspaceId })
            .SingleOrDefaultAsync(cancellationToken);

        if (project is null)
        {
            return Result.Failure<ProjectAgentPermissionsDto?>(
                $"{NotFoundPrefix} project {request.ProjectId} not found");
        }

        // Step 2: workspace-membership gate — same pattern as
        // GetProjectHandler. Workspace owners are members of their own
        // workspace by construction.
        var isMember = await _db.WorkspaceMemberships
            .AsNoTracking()
            .AnyAsync(
                m => m.WorkspaceId == project.WorkspaceId && m.UserId == request.CallerUserId,
                cancellationToken);

        if (!isMember)
        {
            return Result.Failure<ProjectAgentPermissionsDto?>(
                $"{NotFoundPrefix} project {request.ProjectId} not found");
        }

        // Step 3: load the (optional) override row. Absence here is the
        // happy "no override" path — surface a success-with-null result so
        // the controller returns 200 with a null body rather than a 404.
        var row = await _db.ProjectAgentPermissions
            .AsNoTracking()
            .Where(x => x.ProjectId == request.ProjectId)
            .Select(x => new ProjectAgentPermissionsDto(
                x.ProjectId,
                x.PermissionMode,
                x.AllowDangerouslySkipPermissions,
                x.AllowedTools,
                x.DisallowedTools,
                x.AdditionalDirectories))
            .SingleOrDefaultAsync(cancellationToken);

        return Result.Success<ProjectAgentPermissionsDto?>(row);
    }
}

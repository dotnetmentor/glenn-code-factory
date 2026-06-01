using Microsoft.EntityFrameworkCore;
using Source.Features.Workspaces.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Commands.RenameProject;

/// <summary>
/// Handles <see cref="RenameProjectCommand"/>. Single transaction: gate on
/// workspace admin role, load the project, delegate to
/// <c>Project.Rename(string)</c> for validation + event raising, save.
///
/// <para><b>Error shape.</b> Privilege / existence failures use the
/// <see cref="NotFoundPrefix"/> sentinel so the controller maps them to 404
/// uniformly — same "don't leak existence or exact privilege gap" rule the
/// agent-permissions slice uses. Validation failures bubble up as bare error
/// codes (<c>name_required</c> / <c>name_too_long</c>) the controller maps to
/// 400 so the frontend can render them verbatim.</para>
/// </summary>
public sealed class RenameProjectHandler : ICommandHandler<RenameProjectCommand, Result>
{
    /// <summary>
    /// Sentinel prefix the controller matches on to map "no project / no
    /// access / not admin" to 404. Mirrors the agent-permissions handlers.
    /// </summary>
    public const string NotFoundPrefix = "not-found:";

    private readonly ApplicationDbContext _db;
    private readonly ILogger<RenameProjectHandler> _logger;

    public RenameProjectHandler(
        ApplicationDbContext db,
        ILogger<RenameProjectHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Result> Handle(RenameProjectCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CallerUserId))
        {
            return Result.Failure($"{NotFoundPrefix} unauthenticated");
        }

        // Load the project tracked — Rename() mutates it. The global IsDeleted
        // query filter excludes soft-deleted rows so a tombstoned project is
        // treated as not-found.
        var project = await _db.Projects
            .FirstOrDefaultAsync(p => p.Id == request.ProjectId, cancellationToken);

        if (project is null)
        {
            return Result.Failure($"{NotFoundPrefix} project {request.ProjectId} not found");
        }

        // Admin-or-higher gate. Owner (0) and Admin (1) pass; Member (2) does
        // not. We collapse "not a member" and "member but only Member role"
        // into the same not-found sentinel so neither existence nor exact
        // privilege gap is leakable.
        var callerRole = await _db.WorkspaceMemberships
            .AsNoTracking()
            .Where(m => m.WorkspaceId == project.WorkspaceId && m.UserId == request.CallerUserId)
            .Select(m => (WorkspaceRole?)m.Role)
            .SingleOrDefaultAsync(cancellationToken);

        if (callerRole is null || !callerRole.Value.IsAtLeast(WorkspaceRole.Admin))
        {
            return Result.Failure($"{NotFoundPrefix} project {request.ProjectId} not found");
        }

        // Apply each optional field independently — null means "leave alone".
        // Validation + state transitions live on the entity so the invariants
        // (length / range) are enforced in one place.

        if (request.Name is not null)
        {
            var renameResult = project.Rename(request.Name);
            if (!renameResult.IsSuccess)
            {
                return renameResult;
            }
        }

        if (request.PreviewPort is not null)
        {
            var portResult = project.SetPreviewPort(request.PreviewPort.Value);
            if (!portResult.IsSuccess)
            {
                return portResult;
            }
        }

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "UpdateProject: project {ProjectId} updated (name='{Name}', previewPort={PreviewPort}) by {UserId}.",
            request.ProjectId,
            project.Name,
            project.PreviewPort,
            request.CallerUserId);

        return Result.Success();
    }
}

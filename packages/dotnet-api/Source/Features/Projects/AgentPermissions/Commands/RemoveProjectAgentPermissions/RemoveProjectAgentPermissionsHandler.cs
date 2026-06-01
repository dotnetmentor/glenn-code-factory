using Microsoft.EntityFrameworkCore;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.AgentPermissions.Commands.RemoveProjectAgentPermissions;

/// <summary>
/// Handler for <see cref="RemoveProjectAgentPermissionsCommand"/>. Gates on
/// workspace membership, then hard-deletes the override row if it exists.
/// Idempotent — removing a non-existent override is a no-op success so
/// concurrent "stop overriding" clicks don't 404 the second one.
///
/// <para><b>Why hard delete?</b> The override row is config, not data. There
/// is no audit value in keeping a tombstoned row around — the supported
/// "stop overriding" gesture is to drop the row and fall through to system
/// defaults. The entity is intentionally not <c>ISoftDelete</c>.</para>
/// </summary>
public sealed class RemoveProjectAgentPermissionsHandler
    : ICommandHandler<RemoveProjectAgentPermissionsCommand, Result>
{
    /// <summary>
    /// Sentinel prefix the controller matches on to map "no project / no
    /// access" to 404. Mirrors <c>GetProjectHandler.NotFoundPrefix</c>.
    /// </summary>
    public const string NotFoundPrefix = "not-found:";

    private readonly ApplicationDbContext _db;
    private readonly ILogger<RemoveProjectAgentPermissionsHandler> _logger;

    public RemoveProjectAgentPermissionsHandler(
        ApplicationDbContext db,
        ILogger<RemoveProjectAgentPermissionsHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Result> Handle(
        RemoveProjectAgentPermissionsCommand request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CallerUserId))
        {
            return Result.Failure($"{NotFoundPrefix} unauthenticated");
        }

        var project = await _db.Projects
            .AsNoTracking()
            .Where(p => p.Id == request.ProjectId)
            .Select(p => new { p.Id, p.WorkspaceId })
            .SingleOrDefaultAsync(cancellationToken);

        if (project is null)
        {
            return Result.Failure($"{NotFoundPrefix} project {request.ProjectId} not found");
        }

        var isMember = await _db.WorkspaceMemberships
            .AsNoTracking()
            .AnyAsync(
                m => m.WorkspaceId == project.WorkspaceId && m.UserId == request.CallerUserId,
                cancellationToken);

        if (!isMember)
        {
            return Result.Failure($"{NotFoundPrefix} project {request.ProjectId} not found");
        }

        var row = await _db.ProjectAgentPermissions
            .FirstOrDefaultAsync(x => x.ProjectId == request.ProjectId, cancellationToken);

        if (row is null)
        {
            // Idempotent: no override to remove is the desired end state.
            _logger.LogInformation(
                "RemoveProjectAgentPermissions: project {ProjectId} already had no override (no-op).",
                request.ProjectId);
            return Result.Success();
        }

        _db.ProjectAgentPermissions.Remove(row);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "RemoveProjectAgentPermissions: project {ProjectId} override removed by {UserId}.",
            request.ProjectId,
            request.CallerUserId);

        return Result.Success();
    }
}

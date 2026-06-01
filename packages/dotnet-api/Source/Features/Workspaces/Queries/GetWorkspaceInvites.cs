using Microsoft.EntityFrameworkCore;
using Source.Features.Workspaces.Models;
using Source.Infrastructure;
using Source.Infrastructure.Workspaces;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Workspaces.Queries;

/// <summary>
/// List the pending (un-accepted, un-expired) invites for the current workspace.
/// Endpoint must be guarded by <c>[RequireWorkspaceRole(Admin)]</c>.
/// </summary>
public record GetWorkspaceInvitesQuery() : IQuery<Result<IReadOnlyList<WorkspaceInviteItem>>>;

public record WorkspaceInviteItem
{
    public required Guid Id { get; init; }
    public required string Email { get; init; }
    public required WorkspaceRole Role { get; init; }
    public required DateTime ExpiresAt { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required string InvitedById { get; init; }
}

public sealed class GetWorkspaceInvitesHandler : IQueryHandler<GetWorkspaceInvitesQuery, Result<IReadOnlyList<WorkspaceInviteItem>>>
{
    private readonly ApplicationDbContext _db;
    private readonly IWorkspaceContext _wsCtx;

    public GetWorkspaceInvitesHandler(ApplicationDbContext db, IWorkspaceContext wsCtx)
    {
        _db = db;
        _wsCtx = wsCtx;
    }

    public async Task<Result<IReadOnlyList<WorkspaceInviteItem>>> Handle(GetWorkspaceInvitesQuery request, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var items = await _db.WorkspaceInvites
            .AsNoTracking()
            .Where(i => i.WorkspaceId == _wsCtx.Id && i.AcceptedAt == null && i.ExpiresAt > now)
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new WorkspaceInviteItem
            {
                Id = i.Id,
                Email = i.Email,
                Role = i.Role,
                ExpiresAt = i.ExpiresAt,
                CreatedAt = i.CreatedAt,
                InvitedById = i.InvitedById,
            })
            .ToListAsync(cancellationToken);

        return Result.Success<IReadOnlyList<WorkspaceInviteItem>>(items);
    }
}

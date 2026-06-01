using Microsoft.EntityFrameworkCore;
using Source.Features.Workspaces.Models;
using Source.Infrastructure;
using Source.Infrastructure.Workspaces;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Workspaces.Queries;

/// <summary>
/// List the members of the workspace currently in <see cref="IWorkspaceContext"/>.
/// Endpoint must be guarded by <c>[RequireWorkspaceRole(Member)]</c> so the context is populated.
/// </summary>
public record GetWorkspaceMembersQuery() : IQuery<Result<IReadOnlyList<WorkspaceMemberItem>>>;

public record WorkspaceMemberItem
{
    public required string UserId { get; init; }
    public required string Email { get; init; }
    public required WorkspaceRole Role { get; init; }
    public required bool IsOwner { get; init; }
    public required DateTime JoinedAt { get; init; }
}

public sealed class GetWorkspaceMembersHandler : IQueryHandler<GetWorkspaceMembersQuery, Result<IReadOnlyList<WorkspaceMemberItem>>>
{
    private readonly ApplicationDbContext _db;
    private readonly IWorkspaceContext _wsCtx;

    public GetWorkspaceMembersHandler(ApplicationDbContext db, IWorkspaceContext wsCtx)
    {
        _db = db;
        _wsCtx = wsCtx;
    }

    public async Task<Result<IReadOnlyList<WorkspaceMemberItem>>> Handle(GetWorkspaceMembersQuery request, CancellationToken cancellationToken)
    {
        var workspaceId = _wsCtx.Id;
        var ownerId = await _db.Workspaces
            .AsNoTracking()
            .Where(w => w.Id == workspaceId)
            .Select(w => w.OwnerId)
            .SingleAsync(cancellationToken);

        var items = await _db.WorkspaceMemberships
            .AsNoTracking()
            .Where(m => m.WorkspaceId == workspaceId)
            .OrderBy(m => m.Role) // Owner (0) → Admin (1) → Member (2)
            .ThenBy(m => m.User.Email)
            .Select(m => new WorkspaceMemberItem
            {
                UserId = m.UserId,
                Email = m.User.Email!,
                Role = m.Role,
                IsOwner = m.UserId == ownerId,
                JoinedAt = m.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        return Result.Success<IReadOnlyList<WorkspaceMemberItem>>(items);
    }
}

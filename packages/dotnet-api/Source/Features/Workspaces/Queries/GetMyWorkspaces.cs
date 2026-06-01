using Microsoft.EntityFrameworkCore;
using Source.Features.Workspaces.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Workspaces.Queries;

public record GetMyWorkspacesQuery(string UserId) : IQuery<Result<IReadOnlyList<MyWorkspaceItem>>>;

public record MyWorkspaceItem
{
    public required Guid Id { get; init; }
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public required WorkspaceRole Role { get; init; }
    public required bool IsOwner { get; init; }
    public required DateTime CreatedAt { get; init; }
}

public sealed class GetMyWorkspacesHandler : IQueryHandler<GetMyWorkspacesQuery, Result<IReadOnlyList<MyWorkspaceItem>>>
{
    private readonly ApplicationDbContext _db;

    public GetMyWorkspacesHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<IReadOnlyList<MyWorkspaceItem>>> Handle(GetMyWorkspacesQuery request, CancellationToken cancellationToken)
    {
        var items = await _db.WorkspaceMemberships
            .AsNoTracking()
            .Where(m => m.UserId == request.UserId)
            .OrderBy(m => m.Workspace.Name)
            .Select(m => new MyWorkspaceItem
            {
                Id = m.Workspace.Id,
                Slug = m.Workspace.Slug,
                Name = m.Workspace.Name,
                Role = m.Role,
                IsOwner = m.Workspace.OwnerId == m.UserId,
                CreatedAt = m.Workspace.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        return Result.Success<IReadOnlyList<MyWorkspaceItem>>(items);
    }
}

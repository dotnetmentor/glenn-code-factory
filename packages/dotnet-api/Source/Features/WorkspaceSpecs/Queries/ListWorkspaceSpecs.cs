using Microsoft.EntityFrameworkCore;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.WorkspaceSpecs.Queries;

/// <summary>
/// List every catalog spec belonging to a workspace. Caller must be a member
/// of the workspace; non-members get <c>not_a_member</c>. The list view omits
/// <c>Content</c> — the management UI loads full content on demand via
/// <see cref="GetWorkspaceSpecQuery"/>.
/// </summary>
public record ListWorkspaceSpecsQuery(Guid WorkspaceId, string CallerUserId)
    : IQuery<Result<List<WorkspaceSpecListItem>>>;

/// <summary>
/// Lightweight list row — name, description, audit metadata. Excludes
/// <c>Content</c> so the catalog table page can render without pulling
/// potentially-large jsonb blobs.
/// </summary>
public record WorkspaceSpecListItem
{
    public required Guid Id { get; init; }
    public required Guid WorkspaceId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime UpdatedAt { get; init; }
    public required string CreatedByUserId { get; init; }
    public required string UpdatedByUserId { get; init; }
}

public sealed class ListWorkspaceSpecsHandler
    : IQueryHandler<ListWorkspaceSpecsQuery, Result<List<WorkspaceSpecListItem>>>
{
    private readonly ApplicationDbContext _db;

    public ListWorkspaceSpecsHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<List<WorkspaceSpecListItem>>> Handle(
        ListWorkspaceSpecsQuery request,
        CancellationToken cancellationToken)
    {
        // Membership gate — owner / admin / member all pass.
        var isMember = await _db.WorkspaceMemberships
            .AsNoTracking()
            .AnyAsync(
                m => m.WorkspaceId == request.WorkspaceId && m.UserId == request.CallerUserId,
                cancellationToken);

        if (!isMember)
        {
            return Result.Failure<List<WorkspaceSpecListItem>>("not_a_member");
        }

        var items = await _db.WorkspaceSpecs
            .AsNoTracking()
            .Where(s => s.WorkspaceId == request.WorkspaceId)
            .OrderBy(s => s.Name)
            .Select(s => new WorkspaceSpecListItem
            {
                Id = s.Id,
                WorkspaceId = s.WorkspaceId,
                Name = s.Name,
                Description = s.Description,
                CreatedAt = s.CreatedAt,
                UpdatedAt = s.UpdatedAt,
                CreatedByUserId = s.CreatedByUserId,
                UpdatedByUserId = s.UpdatedByUserId,
            })
            .ToListAsync(cancellationToken);

        return Result.Success(items);
    }
}

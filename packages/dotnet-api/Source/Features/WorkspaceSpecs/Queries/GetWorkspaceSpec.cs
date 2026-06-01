using Microsoft.EntityFrameworkCore;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.WorkspaceSpecs.Queries;

/// <summary>
/// Fetch one catalog spec by id, including full <c>Content</c>. Membership-gated:
/// non-members of the URL workspace and cross-workspace id lookups both fail
/// with <c>not_a_member</c> so the controller can map both cases to 403.
/// </summary>
public record GetWorkspaceSpecQuery(Guid WorkspaceId, Guid SpecId, string CallerUserId)
    : IQuery<Result<WorkspaceSpecDetail>>;

/// <summary>
/// Full catalog entry shape — includes <c>Content</c> (the raw V2 RuntimeSpec
/// JSON document). Returned by GetOne / Create / Update / Duplicate.
/// </summary>
public record WorkspaceSpecDetail
{
    public required Guid Id { get; init; }
    public required Guid WorkspaceId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string Content { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime UpdatedAt { get; init; }
    public required string CreatedByUserId { get; init; }
    public required string UpdatedByUserId { get; init; }
}

public sealed class GetWorkspaceSpecHandler
    : IQueryHandler<GetWorkspaceSpecQuery, Result<WorkspaceSpecDetail>>
{
    private readonly ApplicationDbContext _db;

    public GetWorkspaceSpecHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<WorkspaceSpecDetail>> Handle(
        GetWorkspaceSpecQuery request,
        CancellationToken cancellationToken)
    {
        var isMember = await _db.WorkspaceMemberships
            .AsNoTracking()
            .AnyAsync(
                m => m.WorkspaceId == request.WorkspaceId && m.UserId == request.CallerUserId,
                cancellationToken);

        if (!isMember)
        {
            return Result.Failure<WorkspaceSpecDetail>("not_a_member");
        }

        var spec = await _db.WorkspaceSpecs
            .AsNoTracking()
            .SingleOrDefaultAsync(
                s => s.Id == request.SpecId && s.WorkspaceId == request.WorkspaceId,
                cancellationToken);

        if (spec is null)
        {
            // Could be: id doesn't exist OR id exists in another workspace.
            // We don't disambiguate — both look the same from outside.
            return Result.Failure<WorkspaceSpecDetail>("spec_not_found");
        }

        return Result.Success(new WorkspaceSpecDetail
        {
            Id = spec.Id,
            WorkspaceId = spec.WorkspaceId,
            Name = spec.Name,
            Description = spec.Description,
            Content = spec.Content,
            CreatedAt = spec.CreatedAt,
            UpdatedAt = spec.UpdatedAt,
            CreatedByUserId = spec.CreatedByUserId,
            UpdatedByUserId = spec.UpdatedByUserId,
        });
    }
}

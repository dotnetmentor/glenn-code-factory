using Microsoft.EntityFrameworkCore;
using Source.Features.Workspaces.Models;
using Source.Infrastructure;
using Source.Infrastructure.Workspaces;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Workspaces.Queries;

/// <summary>
/// Get details for the current workspace (resolved from <see cref="IWorkspaceContext"/>).
/// Endpoint must be guarded by [RequireWorkspaceRole(Member)] so the context is populated.
/// </summary>
public record GetCurrentWorkspaceQuery() : IQuery<Result<WorkspaceDetailsResponse>>;

public record WorkspaceDetailsResponse
{
    public required Guid Id { get; init; }
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public required string OwnerId { get; init; }
    public required WorkspaceRole CallerRole { get; init; }
    public required int MemberCount { get; init; }
    public required DateTime CreatedAt { get; init; }
}

public sealed class GetCurrentWorkspaceHandler : IQueryHandler<GetCurrentWorkspaceQuery, Result<WorkspaceDetailsResponse>>
{
    private readonly ApplicationDbContext _db;
    private readonly IWorkspaceContext _wsCtx;

    public GetCurrentWorkspaceHandler(ApplicationDbContext db, IWorkspaceContext wsCtx)
    {
        _db = db;
        _wsCtx = wsCtx;
    }

    public async Task<Result<WorkspaceDetailsResponse>> Handle(GetCurrentWorkspaceQuery request, CancellationToken cancellationToken)
    {
        var workspace = await _db.Workspaces
            .AsNoTracking()
            .SingleOrDefaultAsync(w => w.Id == _wsCtx.Id, cancellationToken);

        if (workspace is null)
        {
            return Result.Failure<WorkspaceDetailsResponse>("Workspace not found");
        }

        var memberCount = await _db.WorkspaceMemberships
            .CountAsync(m => m.WorkspaceId == workspace.Id, cancellationToken);

        return Result.Success(new WorkspaceDetailsResponse
        {
            Id = workspace.Id,
            Slug = workspace.Slug,
            Name = workspace.Name,
            OwnerId = workspace.OwnerId,
            CallerRole = _wsCtx.Role,
            MemberCount = memberCount,
            CreatedAt = workspace.CreatedAt,
        });
    }
}

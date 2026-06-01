using Microsoft.EntityFrameworkCore;
using Source.Features.Conversations.Models;
using Source.Infrastructure;
using Source.Infrastructure.Workspaces;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Queries.ListWorkspaceRecentBranches;

/// <summary>
/// Handler for <see cref="ListWorkspaceRecentBranchesQuery"/>. Workspace
/// membership has already been verified by <c>[RequireWorkspaceRole(Member)]</c>;
/// the workspace id arrives via <see cref="IWorkspaceContext"/>. Soft-deleted
/// projects are filtered out by the global query filter on
/// <see cref="Source.Features.Projects.Models.Project"/>, which transitively
/// hides their branches via the <c>ProjectId</c> filter on the join. Archived
/// branches are also excluded — the "Recent work" list is a navigation
/// affordance and should not surface branches the user has archived.
///
/// <para>The whole projection is a single SQL round-trip — Projects ⨝
/// ProjectBranches with two correlated subqueries (one for
/// <c>LastActivityAt</c>, one for <c>RunningTurnCount</c>). No N+1.</para>
///
/// <para>Sort: <c>COALESCE(LastActivityAt, branch UpdatedAt) DESC</c> so the
/// branch the user most recently touched lands at the top, with brand-new
/// branches (no conversations yet) falling back to their creation/update
/// timestamp instead of sorting to the bottom as a giant block of nulls. Limit
/// is clamped to <c>[1, 50]</c> in <see cref="NormaliseLimit"/> with a default
/// of 10.</para>
/// </summary>
public sealed class ListWorkspaceRecentBranchesHandler
    : IQueryHandler<ListWorkspaceRecentBranchesQuery, Result<List<RecentBranchDto>>>
{
    private const int DefaultLimit = 10;
    private const int MaxLimit = 50;

    private readonly ApplicationDbContext _db;
    private readonly IWorkspaceContext _wsCtx;

    public ListWorkspaceRecentBranchesHandler(ApplicationDbContext db, IWorkspaceContext wsCtx)
    {
        _db = db;
        _wsCtx = wsCtx;
    }

    public async Task<Result<List<RecentBranchDto>>> Handle(
        ListWorkspaceRecentBranchesQuery request,
        CancellationToken cancellationToken)
    {
        var workspaceId = _wsCtx.Id;
        var limit = NormaliseLimit(request.Limit);

        // Single SQL round-trip — Project ⨝ Branch filtered by workspace, with
        // two correlated subqueries on Conversations / AgentSessions. The
        // global query filter on Project drops soft-deleted projects (and
        // transitively their branches via the ProjectId filter). Branches are
        // not soft-deletable today (see ProjectBranch.cs), so we only need to
        // exclude archived branches.
        var items = await _db.ProjectBranches
            .AsNoTracking()
            .Where(b => b.Project.WorkspaceId == workspaceId)
            .Where(b => !b.IsArchived)
            .Select(b => new
            {
                Dto = new RecentBranchDto(
                    b.Id,
                    b.Name,
                    b.IsDefault,
                    // LastActivityAt — max of Conversation.LastActivityAt
                    // across all conversations on this branch. Cast to nullable
                    // so the empty-collection case projects NULL instead of
                    // throwing. Mirrors ListProjectBranchesHandler.
                    _db.Conversations
                        .Where(c => c.BranchId == b.Id)
                        .Select(c => (DateTime?)c.LastActivityAt)
                        .Max(),
                    // RunningTurnCount — Pending/Running sessions on this
                    // branch. AgentSession has no direct BranchId, so we walk
                    // through the parent Conversation. Correlated subquery so
                    // the projection stays one round-trip.
                    _db.AgentSessions.Count(s =>
                        s.Conversation.BranchId == b.Id
                        && (s.Status == AgentSessionStatus.Pending
                            || s.Status == AgentSessionStatus.Running)),
                    b.ProjectId,
                    b.Project.Name,
                    b.Project.GithubRepoOwner,
                    b.Project.GithubRepoName),
                // SortKey — COALESCE(LastActivityAt, UpdatedAt) so brand-new
                // branches with zero conversations don't all sort to the
                // bottom as a NULL block. EF Core translates this to a single
                // SQL expression on the server side.
                SortKey = _db.Conversations
                    .Where(c => c.BranchId == b.Id)
                    .Select(c => (DateTime?)c.LastActivityAt)
                    .Max() ?? b.UpdatedAt
            })
            .OrderByDescending(x => x.SortKey)
            .Take(limit)
            .Select(x => x.Dto)
            .ToListAsync(cancellationToken);

        // Return the empty list verbatim — the wire contract is "empty array,
        // never null" so the frontend doesn't need a null-check on .map.
        return Result.Success(items);
    }

    private static int NormaliseLimit(int requested)
    {
        if (requested <= 0) return DefaultLimit;
        if (requested > MaxLimit) return MaxLimit;
        return requested;
    }
}

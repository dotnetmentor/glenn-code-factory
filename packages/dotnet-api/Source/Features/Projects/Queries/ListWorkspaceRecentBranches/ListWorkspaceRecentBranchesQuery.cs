using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Queries.ListWorkspaceRecentBranches;

/// <summary>
/// Read query for <c>GET /api/workspaces/{slug}/branches/recent</c> — returns
/// the flat list of <see cref="RecentBranchDto"/> rows the workspace landing
/// page renders in its "Recent work" list. Clicks land on the exact branch the
/// user last touched (rather than the project root which always redirects to
/// the default branch), so the row links each branch directly.
///
/// <para>The endpoint is gated by <c>[RequireWorkspaceRole(Member)]</c> — the
/// attribute resolves the workspace from the route <c>{slug}</c>, validates the
/// caller's membership, and populates
/// <see cref="Source.Infrastructure.Workspaces.IWorkspaceContext"/>. The
/// handler reads the workspace id from that context, so the query itself
/// carries no slug/tenant fields. <see cref="Limit"/> is normalised in the
/// handler (default 10, max 50) — callers control it via the <c>?limit=</c>
/// query string.</para>
/// </summary>
public sealed record ListWorkspaceRecentBranchesQuery(int Limit) : IQuery<Result<List<RecentBranchDto>>>;

/// <summary>
/// Flat projection of a <see cref="Source.Features.Projects.Models.ProjectBranch"/>
/// joined with its owning <see cref="Source.Features.Projects.Models.Project"/>
/// for the "Recent work" list on the workspace landing page. Flat by design
/// (no nested objects) — Orval consumers and the simple list row both want a
/// single shape.
///
/// <para><see cref="LastActivityAt"/> is the max of
/// <c>Conversation.LastActivityAt</c> across all conversations scoped to this
/// branch — <c>null</c> when the branch has no conversations yet. Sort uses
/// COALESCE(LastActivityAt, branch UpdatedAt) DESC so brand-new branches with
/// zero conversations still appear in the recent list (otherwise they would
/// always sort to the bottom). <see cref="RunningTurnCount"/> is the count of
/// <c>AgentSession</c> rows on this branch's conversations currently in
/// <c>Pending</c> or <c>Running</c> — drives the UI's running pulse. Both are
/// computed via correlated subqueries so the endpoint resolves in a single
/// SQL round-trip — no N+1.</para>
/// </summary>
public sealed record RecentBranchDto(
    Guid BranchId,
    string BranchName,
    bool IsDefault,
    DateTime? LastActivityAt,
    int RunningTurnCount,
    Guid ProjectId,
    string ProjectName,
    string GithubRepoOwner,
    string GithubRepoName);

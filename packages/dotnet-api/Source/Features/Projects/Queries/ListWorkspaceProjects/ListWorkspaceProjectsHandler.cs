using Microsoft.EntityFrameworkCore;
using Source.Features.Conversations.Models;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;
using Source.Infrastructure.Workspaces;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Queries.ListWorkspaceProjects;

/// <summary>
/// Handler for <see cref="ListWorkspaceProjectsQuery"/>. Workspace membership
/// has already been verified by <c>[RequireWorkspaceRole(Member)]</c>; the
/// workspace id arrives via <see cref="IWorkspaceContext"/>. Soft-deleted
/// projects are filtered out by the global query filter on
/// <see cref="Source.Features.Projects.Models.Project"/>.
///
/// <para>Branch count is computed via a correlated subquery
/// (<c>p.Branches.Count()</c>) so the result is a single round-trip — no N+1.
/// Sort is "newest activity first" so the recently-worked-on projects float to
/// the top of the list view.</para>
///
/// <para>Runtime state and the optional failure metadata are aggregated across
/// all non-archived branch runtimes so the sidebar's "Needs Action" bucket
/// reflects any branch that needs attention. Both columns are surfaced via
/// correlated subqueries so the projection still resolves in a single
/// database round-trip.</para>
/// </summary>
public sealed class ListWorkspaceProjectsHandler
    : IQueryHandler<ListWorkspaceProjectsQuery, Result<List<ProjectSummaryDto>>>
{
    private readonly ApplicationDbContext _db;
    private readonly IWorkspaceContext _wsCtx;

    public ListWorkspaceProjectsHandler(ApplicationDbContext db, IWorkspaceContext wsCtx)
    {
        _db = db;
        _wsCtx = wsCtx;
    }

    public async Task<Result<List<ProjectSummaryDto>>> Handle(
        ListWorkspaceProjectsQuery request,
        CancellationToken cancellationToken)
    {
        var workspaceId = _wsCtx.Id;

        var items = await _db.Projects
            .AsNoTracking()
            .Where(p => p.WorkspaceId == workspaceId)
            .OrderByDescending(p => p.UpdatedAt)
            .ThenByDescending(p => p.CreatedAt)
            .Select(p => new ProjectSummaryDto(
                p.Id,
                p.Name,
                p.GithubRepoOwner,
                p.GithubRepoName,
                p.GithubInstallationId,
                p.CreatedAt,
                p.UpdatedAt,
                // BranchCount is a sidebar/navigation signal — archived
                // branches are hidden from the sidebar, so the count must
                // exclude them too. Default branches never archive (the
                // archive command refuses), so this filter preserves the
                // "at least 1" invariant the sidebar relies on.
                p.Branches.Count(b => !b.IsArchived),
                (DateTime?)p.UpdatedAt,
                // Highest-priority runtime state across non-archived branches.
                // Failed/Crashed wins so the sidebar "Needs Action" section
                // fires when ANY branch needs attention, not just the default.
                p.Runtimes.Any(r => !r.Branch.IsArchived
                    && (r.State == RuntimeState.Failed || r.State == RuntimeState.Crashed))
                    ? RuntimeState.Failed
                    : p.Runtimes.Any(r => !r.Branch.IsArchived
                        && (r.State == RuntimeState.Pending
                            || r.State == RuntimeState.Booting
                            || r.State == RuntimeState.Bootstrapping
                            || r.State == RuntimeState.Waking))
                        ? RuntimeState.Booting
                        : p.Runtimes.Any(r => !r.Branch.IsArchived && r.State == RuntimeState.Online)
                            ? RuntimeState.Online
                            : p.Runtimes.Any(r => !r.Branch.IsArchived
                                && (r.State == RuntimeState.Suspended
                                    || r.State == RuntimeState.Suspending))
                                ? RuntimeState.Suspended
                                : (RuntimeState?)null,
                // Failure metadata from the newest Failed runtime on any branch.
                p.Runtimes
                    .Where(r => !r.Branch.IsArchived && r.State == RuntimeState.Failed)
                    .OrderByDescending(r => r.CreatedAt)
                    .Take(1)
                    .SelectMany(r => _db.RuntimeStateEvents
                        .Where(e => e.RuntimeId == r.Id && e.ToState == RuntimeState.Failed)
                        .OrderByDescending(e => e.CreatedAt)
                        .Select(e => e.Metadata)
                        .Take(1))
                    .FirstOrDefault(),
                // RunningTurnCount — sessions in Pending or Running on this
                // project. AgentSession has no direct ProjectId FK, so we walk
                // through the parent Conversation. Correlated subquery so the
                // whole projection still resolves in a single round-trip.
                _db.AgentSessions.Count(s =>
                    s.Conversation.ProjectId == p.Id
                    && (s.Status == AgentSessionStatus.Pending
                        || s.Status == AgentSessionStatus.Running)),
                // PreviewPort — per-project dev-server port (default 5173).
                // Defaulted at the DB level via HasDefaultValue so existing
                // rows backfill cleanly on the AddPreviewPortToProjects
                // migration.
                p.PreviewPort))
            .ToListAsync(cancellationToken);

        return Result.Success(items);
    }
}

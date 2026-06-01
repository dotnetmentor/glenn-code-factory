using Microsoft.EntityFrameworkCore;
using Source.Features.Conversations.Models;
using Source.Features.Projects.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Queries.ListProjectBranches;

/// <summary>
/// Handler for <see cref="ListProjectBranchesQuery"/>. Loads the project to run
/// the workspace-membership gate, then projects all non-soft-deleted branches
/// onto <see cref="ProjectBranchDto"/>.
///
/// <para>Branches are NOT soft-deletable today (see <see cref="ProjectBranch"/>'s
/// summary), so no extra filter is needed beyond the usual EF Core query — but
/// we still order by <c>IsDefault DESC, Name ASC</c> so the default branch
/// always lands at the top of the picker, matching what the frontend pre-selects
/// from the <see cref="ProjectDto.DefaultBranchId"/> response.</para>
/// </summary>
public sealed class ListProjectBranchesHandler
    : IQueryHandler<ListProjectBranchesQuery, Result<List<ProjectBranchDto>>>
{
    /// <summary>
    /// Sentinel prefix the controller matches on to map "no project / no
    /// access" to 404. Mirrors
    /// <c>GetProjectHandler.NotFoundPrefix</c> /
    /// <c>UpdateProjectByokHandler.NotFoundPrefix</c>.
    /// </summary>
    public const string NotFoundPrefix = "not-found:";

    private readonly ApplicationDbContext _db;

    public ListProjectBranchesHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<List<ProjectBranchDto>>> Handle(
        ListProjectBranchesQuery request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CallerUserId))
        {
            return Result.Failure<List<ProjectBranchDto>>($"{NotFoundPrefix} unauthenticated");
        }

        // Soft-deleted projects are filtered out by the global query filter, so
        // they collapse into the not-found branch below.
        var workspaceId = await _db.Projects
            .AsNoTracking()
            .Where(p => p.Id == request.ProjectId)
            .Select(p => (Guid?)p.WorkspaceId)
            .SingleOrDefaultAsync(cancellationToken);

        if (workspaceId is null)
        {
            return Result.Failure<List<ProjectBranchDto>>(
                $"{NotFoundPrefix} project {request.ProjectId} not found");
        }

        // Membership gate — workspace owners are members by construction, so a
        // single AnyAsync covers owner + member cases.
        var isMember = await _db.WorkspaceMemberships
            .AsNoTracking()
            .AnyAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == request.CallerUserId,
                cancellationToken);

        if (!isMember)
        {
            return Result.Failure<List<ProjectBranchDto>>(
                $"{NotFoundPrefix} project {request.ProjectId} not found");
        }

        // Default: hide archived branches from sidebars and pickers. The
        // Settings → Branches tab passes ?includeArchived=true to surface the
        // archived rows so the user can unarchive them. History /
        // conversation queries are not routed through this handler, so this
        // filter only affects navigation contexts.
        var branchesQuery = _db.ProjectBranches
            .AsNoTracking()
            .Where(b => b.ProjectId == request.ProjectId);

        if (!request.IncludeArchived)
        {
            branchesQuery = branchesQuery.Where(b => !b.IsArchived);
        }

        var branches = await branchesQuery
            .OrderByDescending(b => b.IsDefault)
            .ThenBy(b => b.Name)
            .Select(b => new ProjectBranchDto(
                b.Id,
                b.ProjectId,
                b.Name,
                b.IsDefault,
                b.CreatedAt,
                // LastActivityAt — max of LastActivityAt across all
                // conversations on this branch. EF Core translates Max() over
                // a DateTime via .Cast<DateTime?>() so the empty-collection
                // case projects to NULL instead of throwing.
                _db.Conversations
                    .Where(c => c.BranchId == b.Id)
                    .Select(c => (DateTime?)c.LastActivityAt)
                    .Max(),
                // RunningTurnCount — sessions in Pending/Running on this
                // branch. Walked through the parent Conversation since
                // AgentSession has no direct BranchId. Correlated subquery so
                // the list endpoint stays a single round-trip.
                _db.AgentSessions.Count(s =>
                    s.Conversation.BranchId == b.Id
                    && (s.Status == AgentSessionStatus.Pending
                        || s.Status == AgentSessionStatus.Running)),
                // PreviewHostname — LEFT JOIN via the SubdomainAssignment FK
                // (AssignedBranchId). null when the branch has no claim yet
                // or pre-dates the Phase 3 pool. EF translates the ternary
                // into a left join, no .Include needed.
                b.AssignedSubdomain != null ? b.AssignedSubdomain.Hostname : null,
                b.IsArchived,
                b.ArchivedAt))
            .ToListAsync(cancellationToken);

        return Result.Success(branches);
    }
}

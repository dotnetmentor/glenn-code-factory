using Microsoft.EntityFrameworkCore;
using Source.Features.Cloudflare.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Cloudflare.Queries;

/// <summary>
/// List every (non-soft-deleted) <see cref="SubdomainAssignment"/>, newest
/// first. Joins to <c>ProjectBranches</c> and <c>Projects</c> when the row is
/// assigned so the UI can render the owning branch + project without a second
/// round-trip.
///
/// <para><b>Never returns the tunnel token.</b> The projection skips
/// <see cref="SubdomainAssignment.TunnelToken"/> by construction — there's no
/// cleartext path through this query.</para>
/// </summary>
public record GetSubdomainsQuery : IQuery<Result<IReadOnlyList<SubdomainAssignmentDto>>>;

public class GetSubdomainsHandler
    : IQueryHandler<GetSubdomainsQuery, Result<IReadOnlyList<SubdomainAssignmentDto>>>
{
    private readonly ApplicationDbContext _db;

    public GetSubdomainsHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<IReadOnlyList<SubdomainAssignmentDto>>> Handle(
        GetSubdomainsQuery request,
        CancellationToken cancellationToken)
    {
        // Left-join through (Branch -> Project) so unassigned rows still
        // appear with the join fields null. We do the join in LINQ rather
        // than a raw SQL JOIN because the Branch FK isn't enforced at this
        // phase — the relationship is purely an in-query lookup against
        // ProjectBranches / Projects.
        var rows = await (
            from sub in _db.SubdomainAssignments.AsNoTracking()
            join branch in _db.ProjectBranches.AsNoTracking()
                on sub.AssignedBranchId equals (Guid?)branch.Id
                into branchJoin
            from branch in branchJoin.DefaultIfEmpty()
            join project in _db.Projects.AsNoTracking()
                on (branch == null ? (Guid?)null : (Guid?)branch.ProjectId) equals (Guid?)project.Id
                into projectJoin
            from project in projectJoin.DefaultIfEmpty()
            orderby sub.CreatedAt descending
            select new SubdomainAssignmentDto
            {
                Id = sub.Id,
                Hostname = sub.Hostname,
                Subdomain = sub.Subdomain,
                Status = sub.Status,
                CreatedAt = sub.CreatedAt,
                AssignedBranchId = sub.AssignedBranchId,
                AssignedAt = sub.AssignedAt,
                AssignedToBranchName = branch != null ? branch.Name : null,
                AssignedToProjectId = project != null ? project.Id : (Guid?)null,
                AssignedToProjectName = project != null ? project.Name : null,
            }).ToListAsync(cancellationToken);

        return Result.Success<IReadOnlyList<SubdomainAssignmentDto>>(rows);
    }
}

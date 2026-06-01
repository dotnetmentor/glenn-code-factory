using Microsoft.EntityFrameworkCore;
using Source.Features.Cloudflare.Models;
using Source.Features.Workspaces.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Commands.DeleteProject;

/// <summary>
/// Handles <see cref="DeleteProjectCommand"/>. Single transaction: gate on
/// workspace admin role, load the project, delegate to
/// <c>Project.MarkDeleted()</c> for event raising, save. The actual
/// <c>DeletedAt</c> / <c>DeletedBy</c> stamping is driven by
/// <c>ApplicationDbContext.SaveChangesAsync</c> when it sees the entity's
/// <c>IsDeleted</c> flag flip — we never touch the timestamp columns by hand.
///
/// <para><b>Error shape.</b> Privilege / existence failures use the
/// <see cref="NotFoundPrefix"/> sentinel so the controller maps them to 404
/// uniformly — same "don't leak existence or exact privilege gap" rule the
/// rename slice uses.</para>
/// </summary>
public sealed class DeleteProjectHandler : ICommandHandler<DeleteProjectCommand, Result>
{
    /// <summary>
    /// Sentinel prefix the controller matches on to map "no project / no
    /// access / not admin" to 404. Mirrors <see cref="RenameProject.RenameProjectHandler"/>.
    /// </summary>
    public const string NotFoundPrefix = "not-found:";

    private readonly ApplicationDbContext _db;
    private readonly ILogger<DeleteProjectHandler> _logger;

    public DeleteProjectHandler(
        ApplicationDbContext db,
        ILogger<DeleteProjectHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Result> Handle(DeleteProjectCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CallerUserId))
        {
            return Result.Failure($"{NotFoundPrefix} unauthenticated");
        }

        // Load tracked — MarkDeleted() flips IsDeleted, which the DbContext
        // interceptor watches to stamp DeletedAt / DeletedBy. The global
        // !IsDeleted query filter excludes already-tombstoned rows, so a
        // double-delete naturally collapses to the not-found branch below.
        var project = await _db.Projects
            .FirstOrDefaultAsync(p => p.Id == request.ProjectId, cancellationToken);

        if (project is null)
        {
            return Result.Failure($"{NotFoundPrefix} project {request.ProjectId} not found");
        }

        // Admin-or-higher gate, same shape as RenameProjectHandler.
        var callerRole = await _db.WorkspaceMemberships
            .AsNoTracking()
            .Where(m => m.WorkspaceId == project.WorkspaceId && m.UserId == request.CallerUserId)
            .Select(m => (WorkspaceRole?)m.Role)
            .SingleOrDefaultAsync(cancellationToken);

        if (callerRole is null || !callerRole.Value.IsAtLeast(WorkspaceRole.Admin))
        {
            return Result.Failure($"{NotFoundPrefix} project {request.ProjectId} not found");
        }

        project.MarkDeleted();

        // -------- cloudflare-tunnel-preview Phase 3: release subdomains --------
        // Soft-deleting a project effectively kills its branches (branches are
        // not soft-deletable in v1; the project tombstone is the logical
        // delete for every branch underneath). Every still-Assigned subdomain
        // owned by one of those branches must transition to Releasing so
        // Phase 4's Cloudflare-side teardown picks it up. Released rows are
        // NOT returned to the pool — destroy-and-never-reuse per the spec.
        //
        // We load branch IDs first (AsNoTracking) then update the matching
        // assignment rows. Single batched query for the load; tracked entity
        // mutations for the flip so the audit interceptor sees the change.
        var branchIds = await _db.ProjectBranches
            .AsNoTracking()
            .Where(b => b.ProjectId == project.Id)
            .Select(b => b.Id)
            .ToListAsync(cancellationToken);

        if (branchIds.Count > 0)
        {
            var assignments = await _db.SubdomainAssignments
                .Where(s => s.AssignedBranchId != null
                            && branchIds.Contains(s.AssignedBranchId.Value)
                            && s.Status == SubdomainStatus.Assigned)
                .ToListAsync(cancellationToken);

            foreach (var assignment in assignments)
            {
                // null out the FK + AssignedAt so the read-side projection
                // stops showing the (now soft-deleted) branch as the owner.
                // Status flip is the durable signal Phase 4's worker watches.
                assignment.Status = SubdomainStatus.Releasing;
                assignment.AssignedBranchId = null;
                assignment.AssignedAt = null;
            }

            if (assignments.Count > 0)
            {
                _logger.LogInformation(
                    "DeleteProject: marked {Count} subdomain(s) as Releasing for project {ProjectId}.",
                    assignments.Count, project.Id);
            }
        }

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "DeleteProject: project {ProjectId} soft-deleted by {UserId}.",
            request.ProjectId,
            request.CallerUserId);

        return Result.Success();
    }
}

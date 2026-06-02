using MediatR;
using Microsoft.EntityFrameworkCore;
using Source.Features.Cloudflare.Commands;
using Source.Features.Conversations.Models;
using Source.Features.Projects.Models;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Commands.AssignBranchSubdomain;

/// <summary>
/// Handles <see cref="AssignBranchSubdomainCommand"/>. Project + membership
/// gate → check the branch doesn't already have a subdomain → delegate to
/// <see cref="AssignSubdomainToBranchCommand"/> → re-project the branch row
/// to <see cref="ProjectBranchDto"/>.
///
/// <para>The inner <c>AssignSubdomainToBranchHandler</c> already owns the
/// race-safe <c>FOR UPDATE SKIP LOCKED</c> claim, the optional Cloudflare
/// ingress PUT and the <c>pool_empty</c> failure path — we don't reinvent
/// any of it here.</para>
/// </summary>
public sealed class AssignBranchSubdomainHandler
    : ICommandHandler<AssignBranchSubdomainCommand, Result<ProjectBranchDto>>
{
    /// <summary>Stable error code the controller maps to HTTP 404. Existence-safe — covers missing project, missing branch, and non-member caller without leaking which one.</summary>
    public const string NotFoundError = "not_found";

    /// <summary>Stable error code the controller maps to HTTP 409 when the branch already has a subdomain.</summary>
    public const string AlreadyAssignedError = "already_assigned";

    /// <summary>Stable error code the controller maps to HTTP 409 when the pool is exhausted. Mirrors the inner command's literal "pool_empty" sentinel.</summary>
    public const string PoolEmptyError = "pool_empty";

    private readonly ApplicationDbContext _db;
    private readonly IMediator _mediator;
    private readonly ILogger<AssignBranchSubdomainHandler> _logger;

    public AssignBranchSubdomainHandler(
        ApplicationDbContext db,
        IMediator mediator,
        ILogger<AssignBranchSubdomainHandler> logger)
    {
        _db = db;
        _mediator = mediator;
        _logger = logger;
    }

    public async Task<Result<ProjectBranchDto>> Handle(
        AssignBranchSubdomainCommand request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CallerUserId))
        {
            return Result.Failure<ProjectBranchDto>(NotFoundError);
        }

        // -------- 1. Membership gate (existence-safe 404) --------
        // Single round-trip: load project → workspace id, then check the
        // caller is a member. Wrong project id, soft-deleted project and
        // non-member caller all collapse into the same 404 so an attacker
        // can't probe project ids.
        var workspaceId = await _db.Projects
            .AsNoTracking()
            .Where(p => p.Id == request.ProjectId)
            .Select(p => (Guid?)p.WorkspaceId)
            .SingleOrDefaultAsync(cancellationToken);

        if (workspaceId is null)
        {
            return Result.Failure<ProjectBranchDto>(NotFoundError);
        }

        var isMember = await _db.WorkspaceMemberships
            .AsNoTracking()
            .AnyAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == request.CallerUserId,
                cancellationToken);

        if (!isMember)
        {
            return Result.Failure<ProjectBranchDto>(NotFoundError);
        }

        // -------- 2. Branch existence + already-assigned guard --------
        // AsNoTracking — the inner command does its own load + mutate of the
        // SubdomainAssignment row; we just need the branch's current state
        // to short-circuit if it already has one.
        var branch = await _db.ProjectBranches
            .AsNoTracking()
            .Where(b => b.Id == request.BranchId && b.ProjectId == request.ProjectId)
            .Select(b => new
            {
                b.Id,
                ExistingHostname = b.AssignedSubdomain != null ? b.AssignedSubdomain.Hostname : null,
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (branch is null)
        {
            return Result.Failure<ProjectBranchDto>(NotFoundError);
        }

        if (branch.ExistingHostname is not null)
        {
            _logger.LogInformation(
                "AssignBranchSubdomain: branch {BranchId} already has hostname {Hostname}; no-op.",
                request.BranchId, branch.ExistingHostname);
            return Result.Failure<ProjectBranchDto>(AlreadyAssignedError);
        }

        // -------- 3. Delegate to the pool claimer --------
        // The inner handler runs standalone (no ambient tx) → opens its own
        // execution-strategy-wrapped transaction, claims a row with
        // FOR UPDATE SKIP LOCKED, reconciles Cloudflare ingress, commits.
        // pool_empty is the only documented failure surface.
        var assignResult = await _mediator.Send(
            new AssignSubdomainToBranchCommand(request.BranchId),
            cancellationToken);

        if (!assignResult.IsSuccess)
        {
            var inner = assignResult.Error ?? string.Empty;
            _logger.LogWarning(
                "AssignBranchSubdomain: inner claim failed for branch {BranchId}: {Error}",
                request.BranchId, inner);

            // Map the inner literal to our stable code unchanged — same string
            // value, just re-namespaced into the controller's switch.
            return Result.Failure<ProjectBranchDto>(
                string.Equals(inner, "pool_empty", StringComparison.Ordinal)
                    ? PoolEmptyError
                    : inner);
        }

        var claimed = assignResult.Value!;

        _logger.LogInformation(
            "AssignBranchSubdomain: branch {BranchId} (project {ProjectId}) claimed hostname {Hostname} for caller {UserId}.",
            request.BranchId, request.ProjectId, claimed.Hostname, request.CallerUserId);

        // -------- 4. Re-project to ProjectBranchDto --------
        // Match the shape ListProjectBranches returns so the frontend can
        // drop this result straight into the branches cache and the Preview
        // tab re-renders with the new hostname on the very next render.
        var dto = await _db.ProjectBranches
            .AsNoTracking()
            .Where(b => b.Id == request.BranchId)
            .Select(b => new ProjectBranchDto(
                b.Id,
                b.ProjectId,
                b.Name,
                b.IsDefault,
                b.CreatedAt,
                _db.Conversations
                    .Where(c => c.BranchId == b.Id)
                    .Select(c => (DateTime?)c.LastActivityAt)
                    .Max(),
                _db.AgentSessions.Count(s =>
                    s.Conversation.BranchId == b.Id
                    && (s.Status == AgentSessionStatus.Pending
                        || s.Status == AgentSessionStatus.Running)),
                b.AssignedSubdomain != null ? b.AssignedSubdomain.Hostname : null,
                b.IsArchived,
                b.ArchivedAt,
                _db.ProjectRuntimes
                    .Where(r => r.BranchId == b.Id)
                    .OrderByDescending(r => r.CreatedAt)
                    .Select(r => (RuntimeState?)r.State)
                    .FirstOrDefault()))
            .SingleOrDefaultAsync(cancellationToken);

        if (dto is null)
        {
            // Defensive — the branch existed at step 2 and we just claimed a
            // subdomain for it, so this should never trip. Fall back to a
            // hand-constructed dto so the caller gets a clean response.
            return Result.Failure<ProjectBranchDto>(NotFoundError);
        }

        return Result.Success(dto);
    }
}

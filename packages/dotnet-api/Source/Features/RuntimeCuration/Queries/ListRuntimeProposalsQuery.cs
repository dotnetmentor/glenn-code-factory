using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeCuration.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimeCuration.Queries;

/// <summary>
/// List the proposal history for a project, newest first. Drives the proposal
/// timeline in the runtime confirmation panel.
///
/// <list type="bullet">
///   <item><see cref="ProjectId"/> is mandatory — read scope is always
///         project-bounded, the controller passes its path id verbatim.</item>
///   <item><see cref="Status"/> is optional; when set, narrows the list to a
///         single bucket (e.g. "show me only Pending" for the action panel).</item>
///   <item><see cref="Limit"/> defaults at the controller (50). The handler
///         clamps to <c>[1, 200]</c> defensively — a malicious large value
///         can't widen the read past the index-friendly window.</item>
/// </list>
///
/// <para>Soft-deleted rows are filtered out by the global query filter on
/// <see cref="RuntimeProposal"/>; cross-project ids return an empty list (NOT
/// a 404 — the list endpoint is "show me my proposals", an empty result is
/// the correct shape for "I have no proposals here").</para>
/// </summary>
public record ListRuntimeProposalsQuery(
    Guid ProjectId,
    RuntimeProposalStatus? Status,
    int Limit) : IQuery<Result<List<RuntimeProposalDto>>>;

public class ListRuntimeProposalsQueryHandler
    : IQueryHandler<ListRuntimeProposalsQuery, Result<List<RuntimeProposalDto>>>
{
    private readonly ApplicationDbContext _db;

    public ListRuntimeProposalsQueryHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<List<RuntimeProposalDto>>> Handle(
        ListRuntimeProposalsQuery request,
        CancellationToken cancellationToken)
    {
        var query = _db.RuntimeProposals
            .AsNoTracking()
            .Where(p => p.ProjectId == request.ProjectId);

        if (request.Status.HasValue)
        {
            var status = request.Status.Value;
            query = query.Where(p => p.Status == status);
        }

        // Defence in depth — even if the controller misbehaves, the handler
        // never reads past a sane window. Clamp to [1, 200].
        var take = Math.Clamp(request.Limit, 1, 200);

        var rows = await query
            .OrderByDescending(p => p.CreatedAt)
            .Take(take)
            .Select(p => new RuntimeProposalDto(
                p.Id,
                p.ProjectId,
                p.RuntimeId,
                p.Status,
                p.ProposedSpec,
                p.AppliedSpec,
                p.Reason,
                p.DecidedBy,
                p.DecidedAt,
                p.ErrorMessage,
                p.CreatedAt))
            .ToListAsync(cancellationToken);

        return Result.Success(rows);
    }
}

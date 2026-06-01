using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeCuration.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimeCuration.Queries;

/// <summary>
/// Single-proposal lookup, project-scoped. Returns the same DTO shape the
/// decision endpoints emit so the frontend can drive the detail card off
/// either the list-then-pick flow or a deep-link.
///
/// <para><b>Cross-project leak prevention.</b> The handler filters by both
/// <see cref="ProjectId"/> and <see cref="ProposalId"/>; a proposal that
/// belongs to a different project collapses to <c>not_found</c> (NOT 403),
/// same convention as <see cref="Commands.ApproveProposalCommandHandler"/> +
/// the Spec 15 Kanban MCP — we never confirm a proposal id exists in some
/// other tenant's data.</para>
/// </summary>
public record GetRuntimeProposalQuery(
    Guid ProjectId,
    Guid ProposalId) : IQuery<Result<RuntimeProposalDto>>;

public class GetRuntimeProposalQueryHandler
    : IQueryHandler<GetRuntimeProposalQuery, Result<RuntimeProposalDto>>
{
    private readonly ApplicationDbContext _db;

    public GetRuntimeProposalQueryHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<RuntimeProposalDto>> Handle(
        GetRuntimeProposalQuery request,
        CancellationToken cancellationToken)
    {
        var dto = await _db.RuntimeProposals
            .AsNoTracking()
            .Where(p => p.ProjectId == request.ProjectId && p.Id == request.ProposalId)
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
            .FirstOrDefaultAsync(cancellationToken);

        return dto is null
            ? Result.Failure<RuntimeProposalDto>("not_found")
            : Result.Success(dto);
    }
}

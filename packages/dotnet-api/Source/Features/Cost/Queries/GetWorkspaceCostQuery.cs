using Microsoft.EntityFrameworkCore;
using Source.Features.Cost.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Cost.Queries;

/// <summary>
/// Sum cost + token usage across every session under any project in this
/// workspace. Joins through
/// <c>AgentSession → Conversation → ProjectBranch → Project.WorkspaceId</c>.
/// Global query filters on Project (soft-delete) and Conversation (archived)
/// apply naturally so a deleted-but-not-purged project doesn't appear in the
/// rollup.
/// </summary>
public record GetWorkspaceCostQuery(Guid WorkspaceId) : IQuery<Result<CostSummaryResponse>>;

public sealed class GetWorkspaceCostHandler
    : IQueryHandler<GetWorkspaceCostQuery, Result<CostSummaryResponse>>
{
    private readonly ApplicationDbContext _db;

    public GetWorkspaceCostHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<CostSummaryResponse>> Handle(
        GetWorkspaceCostQuery request,
        CancellationToken cancellationToken)
    {
        var summary = await _db.AgentSessions
            .Where(s => s.Conversation.Branch.Project.WorkspaceId == request.WorkspaceId)
            .GroupBy(_ => 1)
            .Select(g => new CostSummaryResponse(
                g.Sum(s => s.TotalCostUsd ?? 0m),
                g.Sum(s => (long)(s.InputTokens ?? 0)),
                g.Sum(s => (long)(s.OutputTokens ?? 0)),
                g.Sum(s => (long)(s.CacheReadTokens ?? 0)),
                g.Sum(s => (long)(s.CacheWriteTokens ?? 0)),
                g.Sum(s => (long)(s.ReasoningTokens ?? 0)),
                g.Count()))
            .FirstOrDefaultAsync(cancellationToken);

        return Result.Success(summary ?? new CostSummaryResponse(0m, 0, 0, 0, 0, 0, 0));
    }
}

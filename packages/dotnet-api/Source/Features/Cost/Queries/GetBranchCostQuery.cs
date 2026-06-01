using Microsoft.EntityFrameworkCore;
using Source.Features.Cost.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Cost.Queries;

/// <summary>
/// Sum cost + token usage across every session under any conversation in this
/// branch. Joins through <c>AgentSession → Conversation.BranchId</c>.
/// </summary>
public record GetBranchCostQuery(Guid BranchId) : IQuery<Result<CostSummaryResponse>>;

public sealed class GetBranchCostHandler
    : IQueryHandler<GetBranchCostQuery, Result<CostSummaryResponse>>
{
    private readonly ApplicationDbContext _db;

    public GetBranchCostHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<CostSummaryResponse>> Handle(
        GetBranchCostQuery request,
        CancellationToken cancellationToken)
    {
        var summary = await _db.AgentSessions
            .Where(s => s.Conversation.BranchId == request.BranchId)
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

using Microsoft.EntityFrameworkCore;
using Source.Features.Cost.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Cost.Queries;

/// <summary>
/// Sum cost + token usage across every <see cref="Source.Features.Conversations.Models.AgentSession"/>
/// belonging to a conversation. Cheap — the FK-indexed
/// <c>(ConversationId, CreatedAt)</c> covers the scan.
/// </summary>
public record GetConversationCostQuery(Guid ConversationId) : IQuery<Result<CostSummaryResponse>>;

public sealed class GetConversationCostHandler
    : IQueryHandler<GetConversationCostQuery, Result<CostSummaryResponse>>
{
    private readonly ApplicationDbContext _db;

    public GetConversationCostHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<CostSummaryResponse>> Handle(
        GetConversationCostQuery request,
        CancellationToken cancellationToken)
    {
        // Null-coalesce in the projection so nullable columns sum cleanly.
        // Server-side aggregation — no fetch of session rows. SessionCount
        // includes sessions that haven't terminated yet (cost columns still
        // null) so the panel can show "12 turns" before any of them have a
        // cost stamped.
        var summary = await _db.AgentSessions
            .Where(s => s.ConversationId == request.ConversationId)
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

        // No sessions yet: return a zero-shaped summary rather than null so the
        // frontend doesn't have to special-case "conversation exists but empty".
        return Result.Success(summary ?? new CostSummaryResponse(0m, 0, 0, 0, 0, 0, 0));
    }
}

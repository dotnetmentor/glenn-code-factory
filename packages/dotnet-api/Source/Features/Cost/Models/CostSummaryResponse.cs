namespace Source.Features.Cost.Models;

/// <summary>
/// Aggregate of per-session cost / token usage at any level of the parent
/// chain (Conversation → Branch → Project → Workspace). All token counts are
/// <see cref="long"/> — individual session counts are <see cref="int"/>-shaped
/// but a workspace-level rollup across thousands of sessions can comfortably
/// exceed 2 billion, so the aggregate widens.
///
/// <para><see cref="SessionCount"/> counts ALL non-deleted sessions in scope,
/// not just those with cost data. That's the metric a "how much have we used"
/// panel wants — a freshly-created session that hasn't terminated yet has null
/// cost columns but still belongs in the denominator.</para>
/// </summary>
public record CostSummaryResponse(
    decimal TotalCostUsd,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long ReasoningTokens,
    int SessionCount);

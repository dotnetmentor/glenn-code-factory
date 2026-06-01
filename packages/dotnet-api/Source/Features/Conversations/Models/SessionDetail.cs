namespace Source.Features.Conversations.Models;

public record SessionDetail(
    Guid Id,
    Guid ConversationId,
    AgentSessionStatus Status,
    string Prompt,
    string? AgentId,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string? FailureReason,
    DateTime CreatedAt,
    int EventCount,
    decimal? CostUsd,
    int? InputTokens,
    int? OutputTokens,
    int? CacheReadTokens,
    int? CacheWriteTokens,
    int? ReasoningTokens);

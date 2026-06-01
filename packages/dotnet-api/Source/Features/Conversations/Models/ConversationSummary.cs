namespace Source.Features.Conversations.Models;

/// <summary>
/// Compact, list-view shape returned by
/// <c>GET /api/projects/{projectId}/conversations</c>. Carries the denormalized
/// columns the conversation list renders (title, last activity, event count) plus
/// the latest session's status so the UI can show a "running / failed" pip without
/// a follow-up round trip per row.
///
/// <para><see cref="LatestSessionStatus"/> is nullable: a freshly-created
/// conversation has no sessions yet and the UI distinguishes "empty" from
/// "pending".</para>
/// </summary>
public record ConversationSummary(
    Guid Id,
    Guid ProjectId,
    Guid BranchId,
    string Title,
    ConversationStatus Status,
    DateTime LastActivityAt,
    int EventCount,
    AgentSessionStatus? LatestSessionStatus,
    DateTime CreatedAt);

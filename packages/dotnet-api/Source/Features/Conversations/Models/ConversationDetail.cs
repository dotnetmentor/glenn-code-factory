namespace Source.Features.Conversations.Models;

/// <summary>
/// Detail shape returned by <c>GET /api/conversations/{id}</c>. Embeds the full
/// session list (oldest first) so the chat panel can render the conversation
/// without a follow-up call. <see cref="IgnoreQueryFilters"/> is applied at the
/// query site so admins/audit can pull archived conversations directly by id.
/// </summary>
public record ConversationDetail(
    Guid Id,
    Guid ProjectId,
    Guid BranchId,
    string Title,
    ConversationStatus Status,
    DateTime LastActivityAt,
    int EventCount,
    DateTime CreatedAt,
    List<SessionSummary> Sessions);

/// <summary>
/// Embedded session row inside <see cref="ConversationDetail"/>. The shape matches
/// what the chat panel needs to render the per-session header (status pip, prompt,
/// timing, optional failure reason). Events themselves load lazily via
/// <c>GET /api/sessions/{id}/events</c>.
///
/// <para>Cost / token columns are denormalized from the SDK's terminal
/// <c>result</c> frame by <c>RecordSessionCostHandler</c>. All nullable — null
/// while the session is still running, or for canceled sessions that never
/// emitted a result frame, or for legacy sessions that pre-date cost tracking.
/// The frontend renders a per-turn cost badge from these columns.</para>
///
/// <para>chat-file-attachments — <see cref="Attachments"/> carries the file
/// metadata for any attachments the user sent on this turn (joined from
/// <c>Attachments</c> by <c>SessionId</c>). Empty list when none were sent.
/// Past-message chips read filename + size directly from these rows so the
/// chat history renders the chip without an N+1 round-trip; the freshly-minted
/// presigned download URL is fetched lazily on click via
/// <c>GET /api/attachments/{id}</c>.</para>
/// </summary>
public record SessionSummary(
    Guid Id,
    AgentSessionStatus Status,
    string Prompt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string? FailureReason,
    DateTime CreatedAt,
    decimal? CostUsd,
    int? InputTokens,
    int? OutputTokens,
    int? CacheReadTokens,
    int? CacheWriteTokens,
    int? ReasoningTokens,
    List<AttachmentSummary> Attachments);

/// <summary>
/// Lean per-attachment metadata embedded inside a <see cref="SessionSummary"/>
/// so past-message chip rendering can show filename + size without a follow-up
/// fetch per chip. Excludes the presigned download URL — that's regenerated on
/// demand (~24h TTL) via <c>GET /api/attachments/{id}</c> when the user
/// actually clicks a chip, so the history payload doesn't bake in expiring
/// URLs and the round-trip stays small.
/// </summary>
public record AttachmentSummary(
    Guid Id,
    string FileName,
    long SizeBytes,
    string? ContentType);

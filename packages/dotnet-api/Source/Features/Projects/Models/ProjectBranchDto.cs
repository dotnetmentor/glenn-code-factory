namespace Source.Features.Projects.Models;

/// <summary>
/// Wire shape for a single <see cref="ProjectBranch"/> row returned by
/// <c>GET /api/projects/{projectId}/branches</c>. Flat by design — the project
/// workspace shell page only needs id + display name + the default-branch flag
/// to populate its branch picker; everything richer (commit SHAs, runtime
/// status per branch, etc.) lives behind dedicated endpoints in follow-up
/// cards.
///
/// <para><see cref="LastActivityAt"/> is the max of <c>Conversation.LastActivityAt</c>
/// across all conversations scoped to this branch (<c>null</c> when the branch
/// has no conversations yet). Lets the agent-native sidebar sort / group
/// branches by recency without a separate per-branch fetch.</para>
///
/// <para><see cref="RunningTurnCount"/> is the count of <c>AgentSession</c>
/// rows on this branch's conversations currently in <c>Pending</c> or
/// <c>Running</c> status — drives the live "in flight" badge in the branch
/// picker / sidebar. Both are computed via correlated subqueries so the list
/// endpoint stays a single round-trip.</para>
///
/// <para><see cref="PreviewHostname"/> is the fully-qualified hostname of the
/// preview subdomain assigned to this branch (e.g. <c>wpxdludx.glenncode.cc</c>),
/// via <c>ProjectBranch.AssignedSubdomain.Hostname</c>. <c>null</c> for
/// branches that pre-date the cloudflare-tunnel-preview Phase 3 pool or that
/// haven't claimed yet. The frontend renders <c>https://{PreviewHostname}</c>
/// inside the AppContainer's Preview tab iframe.</para>
/// </summary>
public record ProjectBranchDto(
    Guid Id,
    Guid ProjectId,
    string Name,
    bool IsDefault,
    DateTime CreatedAt,
    DateTime? LastActivityAt,
    int RunningTurnCount,
    string? PreviewHostname,
    bool IsArchived,
    DateTime? ArchivedAt
);

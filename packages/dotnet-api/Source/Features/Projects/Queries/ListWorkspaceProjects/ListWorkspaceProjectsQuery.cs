using Source.Features.RuntimeLifecycle.Models;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Queries.ListWorkspaceProjects;

/// <summary>
/// Read query for <c>GET /api/workspaces/{slug}/projects</c> — returns the flat
/// list of <see cref="ProjectSummaryDto"/> rows the workspace shell sidebar and
/// landing page need.
///
/// <para>The endpoint is gated by <c>[RequireWorkspaceRole(Member)]</c> — the
/// attribute resolves the workspace from the route <c>{slug}</c>, validates the
/// caller's membership, and populates <see cref="Source.Infrastructure.Workspaces.IWorkspaceContext"/>.
/// The handler reads the workspace id from that context, so the query itself
/// carries no slug/tenant fields.</para>
/// </summary>
public sealed record ListWorkspaceProjectsQuery() : IQuery<Result<List<ProjectSummaryDto>>>;

/// <summary>
/// Minimal projection of a <see cref="Source.Features.Projects.Models.Project"/>
/// for list views. No secrets / no encrypted columns. <see cref="BranchCount"/>
/// is computed by a correlated SQL <c>COUNT</c> so we don't N+1 the branch
/// table. <see cref="LatestActivityAt"/> defaults to the row's
/// <see cref="UpdatedAt"/> until there's a dedicated activity timestamp — the
/// shape stays stable so the frontend can sort by recency without re-plumbing.
///
/// <para><see cref="RuntimeState"/> is the highest-priority runtime state
/// across all non-archived branch runtimes — Failed/Crashed wins over
/// booting states, which win over Online/Suspended — so the sidebar's
/// "Needs Action" bucket reflects any branch that needs attention, not just
/// the default branch. <c>null</c> when no branch has been provisioned
/// yet.</para>
///
/// <para><see cref="RuntimeErrorMessage"/> is populated only when
/// <see cref="RuntimeState"/> is <see cref="Source.Features.RuntimeLifecycle.Models.RuntimeState.Failed"/>;
/// the value is the <c>Metadata</c> from the most recent transition into
/// <c>Failed</c> (mirrors the convention used by
/// <c>RuntimeStatusController</c>). Lets the sidebar surface a tooltip with
/// the failure reason in a follow-up iteration; <c>null</c> in every other
/// state.</para>
///
/// <para><see cref="RunningTurnCount"/> is the count of <c>AgentSession</c>
/// rows on this project currently in <c>Pending</c> or <c>Running</c> status —
/// lets the sidebar distinguish "runtime online but idle" from "runtime online
/// and an agent is actually executing right now", which drives the live "in
/// flight" pulse in the agent-native grouping. Computed via correlated
/// subquery so the projection stays a single round-trip.</para>
/// </summary>
public sealed record ProjectSummaryDto(
    Guid Id,
    string Name,
    string GithubRepoOwner,
    string GithubRepoName,
    /// <summary>
    /// FK to the <c>GithubInstallation</c> this project is currently attached
    /// to. <c>null</c> means the project is <b>detached</b> — projects survive
    /// a GitHub installation disconnect via the SetNull cascade on the FK, and
    /// can be reattached via the <c>ReconnectProjects</c> endpoint. The
    /// frontend gates the inline "GitHub disconnected — Reconnect" affordance
    /// (badge on the project card / sidebar row / landing tile) on this field
    /// being null.
    /// </summary>
    Guid? GithubInstallationId,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int BranchCount,
    DateTime? LatestActivityAt,
    RuntimeState? RuntimeState,
    string? RuntimeErrorMessage,
    int RunningTurnCount,
    // PreviewPort — cloudflare-tunnel-preview Phase 2 per-project dev-server
    // port. Surfaced on the list projection so the workspace projects view
    // can render the configured value without a per-row detail fetch.
    int PreviewPort);

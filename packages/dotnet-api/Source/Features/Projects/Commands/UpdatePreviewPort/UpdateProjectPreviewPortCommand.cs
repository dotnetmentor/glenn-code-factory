using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Commands.UpdatePreviewPort;

/// <summary>
/// Hot-swap a project's preview port. Backs <c>PATCH /api/projects/{projectId}/preview-port</c>.
///
/// <para><b>Why a dedicated command (separate from <c>RenameProjectCommand</c>).</b>
/// The generic <c>PATCH /api/projects/{projectId}</c> path persists the column
/// but does NOT propagate the change to the live Cloudflare tunnels — it's a
/// pure DB write. This command exists to drive the realtime side-effects:</para>
/// <list type="number">
///   <item>Persist the new port on the <c>Project</c> row.</item>
///   <item>Fan out an idempotent Cloudflare ingress PUT to every branch tunnel
///         that's already assigned — the new port takes effect at the edge
///         within ~1s without restarting <c>cloudflared</c> on the daemon side
///         (the local conf is just <c>cloudflared tunnel run --token …</c> —
///         routing lives entirely on Cloudflare's edge).</item>
///   <item>Push a <c>PreviewPortChanged</c> SignalR notification so every
///         open tab in the project / workspace updates its settings view live.</item>
/// </list>
///
/// <para><b>Not a domain event.</b> Port is an infrastructure config column,
/// not a business state transition. The SignalR push is the only outbound
/// notification — no <c>RaiseDomainEvent</c>, no <c>StoredDomainEvents</c> row.</para>
///
/// <para><b>Partial failure tolerance.</b> The DB row is the source of truth.
/// If a Cloudflare PUT fails mid-fan-out we log it and report the failure
/// count on the response, but we DO NOT roll back the DB — the next call
/// catches up the still-stale tunnel.</para>
/// </summary>
public sealed record UpdateProjectPreviewPortCommand(
    Guid ProjectId,
    int Port
) : ICommand<Result<UpdateProjectPreviewPortResponse>>;

/// <summary>
/// Response shape for <see cref="UpdateProjectPreviewPortCommand"/>. Carries
/// the persisted port plus how many of the project's branch tunnels were
/// successfully repointed at the new port (and how many failed). The frontend
/// can surface "1 of 3 tunnels failed — they will retry on next port change"
/// from these counters if it wants — but the common case is
/// <c>TunnelsFailed = 0</c>.
/// </summary>
public sealed record UpdateProjectPreviewPortResponse(
    Guid ProjectId,
    int Port,
    int TunnelsUpdated,
    int TunnelsFailed);

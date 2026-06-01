using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeLifecycle.Events;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;
using Source.Infrastructure;
using Source.Shared.Events;

namespace Source.Features.SignalR.EventHandlers;

/// <summary>
/// Bridges the domain layer to connected React clients: every time a
/// <see cref="RuntimeStateChanged"/> event flows through the dispatcher we
/// fan it out to the <c>project-{id}</c> SignalR group as a
/// <see cref="RuntimeStateChangedNotification"/>.
///
/// <para>Sibling to <c>PersistRuntimeStateEventHandler</c>: that handler writes
/// the audit row, this one drives the live UI. Both react to the same domain
/// event independently — order between them doesn't matter because neither
/// depends on the other's side effects.</para>
///
/// <para>Also fans out to the parallel <c>workspace-{workspaceId}</c> group so
/// the agent-native sidebar can react to runtime status changes on ANY project
/// in the workspace — not just the one in the active tab. The payload includes
/// <c>ProjectId</c> so the client can route the broadcast to the right sidebar
/// row on receipt.</para>
///
/// <para>Hub broadcast failures are intentionally swallowed. The runtime state
/// has already been persisted (the event would not have been raised otherwise),
/// so a transient SignalR fault must not poison the rest of the dispatcher
/// chain. Worst case clients miss a live tick and refresh via the runtime
/// status endpoint.</para>
/// </summary>
public class BroadcastRuntimeStateChangedHandler : IEventHandler<RuntimeStateChanged>
{
    private readonly IHubContext<AgentHub, IAgentClient> _hub;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<BroadcastRuntimeStateChangedHandler> _logger;

    public BroadcastRuntimeStateChangedHandler(
        IHubContext<AgentHub, IAgentClient> hub,
        ApplicationDbContext db,
        ILogger<BroadcastRuntimeStateChangedHandler> logger)
    {
        _hub = hub;
        _db = db;
        _logger = logger;
    }

    public async Task Handle(RuntimeStateChanged notification, CancellationToken cancellationToken)
    {
        var payload = new RuntimeStateChangedNotification(
            RuntimeId: notification.RuntimeId,
            ProjectId: notification.ProjectId,
            FromState: notification.FromState?.ToString(),
            ToState: notification.ToState.ToString(),
            Reason: notification.Reason,
            ChangedAt: notification.OccurredAt,
            ErrorMessage: notification.Metadata);

        try
        {
            await _hub.Clients
                .Group($"branch-{notification.BranchId}")
                .RuntimeStateChanged(payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to broadcast RuntimeStateChanged to branch group for runtime {RuntimeId} (branch {BranchId}, project {ProjectId}); persistence is unaffected.",
                notification.RuntimeId,
                notification.BranchId,
                notification.ProjectId);
        }

        // Additive workspace-group broadcast. Resolve the workspace from the
        // project so the sidebar (which subscribes per-workspace, not per-
        // project) can refresh the affected row in real time. Failures here
        // are swallowed for the same reason the project-group broadcast above
        // is — persistence has already happened, missing a live tick is
        // acceptable.
        try
        {
            var workspaceId = await _db.Projects
                .AsNoTracking()
                .Where(p => p.Id == notification.ProjectId)
                .Select(p => (Guid?)p.WorkspaceId)
                .FirstOrDefaultAsync(cancellationToken);
            if (workspaceId is { } wsId)
            {
                await _hub.Clients
                    .Group($"workspace-{wsId}")
                    .RuntimeStateChanged(payload);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to broadcast RuntimeStateChanged to workspace group for runtime {RuntimeId} (project {ProjectId}); persistence is unaffected.",
                notification.RuntimeId,
                notification.ProjectId);
        }
    }
}

using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using TypedSignalR.Client;

namespace Source.Features.SignalR.Hubs;

/// <summary>
/// React-facing planning hub. One connection per browser tab on a planning
/// surface (kanban board, spec list, spec detail). JWT-authenticated via the
/// default user scheme — same as <see cref="AgentHub"/>.
///
/// <para><b>No auto-join on connect.</b> Unlike <see cref="AgentHub"/> (which
/// auto-joins per-user / per-branch on connect from the negotiate query string),
/// a single planning tab may watch multiple projects (the kanban board hops
/// between projects without reconnecting). The frontend explicitly calls
/// <see cref="JoinProject"/> / <see cref="LeaveProject"/> on mount / unmount of
/// each planning surface. Lifecycle hooks here are minimal — connection-level
/// logging only; SignalR cleans group memberships automatically on disconnect.</para>
///
/// <para><b>Auth gate on <see cref="JoinProject"/>.</b> No project-membership
/// model exists yet in this codebase — the closest existing pattern (project-
/// ownership) is tagged TODO across <see cref="AgentHub"/>. Following that
/// precedent we accept any authenticated user — admin / dev mode. Deviation
/// noted in the card summary; tighten when the Project entity gains an
/// ownership / membership column.</para>
///
/// <para>Outbound broadcasts come from event handlers in the owning feature
/// (Specifications / ProjectKanban), not from this hub class. The hub itself
/// is pure subscribe / unsubscribe wire.</para>
/// </summary>
[Authorize]
public class PlanningHub : Hub<IPlanningClient>, IPlanningHub
{
    private readonly ILogger<PlanningHub> _logger;

    public PlanningHub(ILogger<PlanningHub> logger)
    {
        _logger = logger;
    }

    public override Task OnConnectedAsync()
    {
        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            // [Authorize] should have rejected — defense in depth.
            _logger.LogWarning(
                "PlanningHub connection {ConnectionId} authenticated but missing NameIdentifier claim; aborting.",
                Context.ConnectionId);
            Context.Abort();
            return Task.CompletedTask;
        }

        _logger.LogInformation(
            "PlanningHub connected. User {UserId}, Connection {ConnectionId}",
            userId, Context.ConnectionId);

        return base.OnConnectedAsync();
    }

    public async Task JoinProject(Guid projectId)
    {
        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            // [Authorize] should have rejected — defense in depth.
            throw new HubException("Unauthenticated");
        }

        if (projectId == Guid.Empty)
        {
            throw new HubException("Invalid projectId");
        }

        // TODO(project-ownership): when the Project entity owner / membership
        // column lands, verify the caller has access to this project before
        // joining the group. Today's behavior matches AgentHub's project auto-
        // join — authenticated user only.

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            $"project:{projectId}",
            Context.ConnectionAborted);

        _logger.LogInformation(
            "PlanningHub.JoinProject: connection {ConnectionId} (user {UserId}) joined project:{ProjectId}.",
            Context.ConnectionId, userId, projectId);
    }

    public async Task LeaveProject(Guid projectId)
    {
        if (projectId == Guid.Empty)
        {
            throw new HubException("Invalid projectId");
        }

        await Groups.RemoveFromGroupAsync(
            Context.ConnectionId,
            $"project:{projectId}",
            Context.ConnectionAborted);

        _logger.LogDebug(
            "PlanningHub.LeaveProject: connection {ConnectionId} left project:{ProjectId}.",
            Context.ConnectionId, projectId);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation(exception,
            "PlanningHub disconnected. Connection {ConnectionId}", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}

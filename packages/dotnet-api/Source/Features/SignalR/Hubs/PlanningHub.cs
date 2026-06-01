using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Source.Infrastructure;
using Source.Infrastructure.Extensions;
using TypedSignalR.Client;

namespace Source.Features.SignalR.Hubs;

/// <summary>
/// React-facing planning hub. One connection per browser tab on a planning
/// surface (kanban board, spec list, spec detail). JWT-authenticated via the
/// default user scheme — same as <see cref="AgentHub"/>.
/// </summary>
[Authorize]
public class PlanningHub : Hub<IPlanningClient>, IPlanningHub
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<PlanningHub> _logger;

    public PlanningHub(ApplicationDbContext db, ILogger<PlanningHub> logger)
    {
        _db = db;
        _logger = logger;
    }

    public override Task OnConnectedAsync()
    {
        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
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
            throw new HubException("Unauthenticated");
        }

        if (projectId == Guid.Empty)
        {
            throw new HubException("Invalid projectId");
        }

        if (Context.User is null
            || !await _db.CallerCanAccessProjectAsync(Context.User, projectId, Context.ConnectionAborted))
        {
            throw new HubException("Project not found");
        }

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

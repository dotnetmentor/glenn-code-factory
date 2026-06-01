using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Source.Features.ProjectKanban.Events;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;
using Source.Infrastructure;
using Source.Shared.Events;

namespace Source.Features.ProjectKanban.EventHandlers;

/// <summary>
/// Bridges the domain layer to React clients for subtask creation. The
/// <see cref="SubtaskCreated"/> domain event only carries
/// <see cref="SubtaskCreated.CardId"/> — the parent card's <c>ProjectId</c> is
/// looked up here with a cheap single-column projection so the broadcast can
/// be scoped to the right <c>project:{projectId}</c> SignalR group (the
/// frontend joins per-project, not per-card).
///
/// <para><c>IgnoreQueryFilters()</c> is intentional: a subtask whose parent
/// card was just soft-deleted in the same unit of work would otherwise resolve
/// to a NULL projectId and the broadcast would silently drop. The Created
/// event itself is improbable on a deleted card — defensive read.</para>
///
/// <para>Mirrors <c>BroadcastAgentEventHandler</c> — exception-swallowing
/// reliability contract, persistence is independent of the broadcast.</para>
/// </summary>
public class BroadcastSubtaskCreatedHandler : IEventHandler<SubtaskCreated>
{
    private readonly IHubContext<PlanningHub, IPlanningClient> _hub;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<BroadcastSubtaskCreatedHandler> _logger;

    public BroadcastSubtaskCreatedHandler(
        IHubContext<PlanningHub, IPlanningClient> hub,
        ApplicationDbContext db,
        ILogger<BroadcastSubtaskCreatedHandler> logger)
    {
        _hub = hub;
        _db = db;
        _logger = logger;
    }

    public async Task Handle(SubtaskCreated notification, CancellationToken cancellationToken)
    {
        var projectId = await _db.ProjectKanbanCards
            .IgnoreQueryFilters()
            .Where(c => c.Id == notification.CardId)
            .Select(c => (Guid?)c.ProjectId)
            .FirstOrDefaultAsync(cancellationToken);

        if (projectId is null)
        {
            _logger.LogDebug(
                "BroadcastSubtaskCreatedHandler: orphan subtask event — no card {CardId} found for subtask {SubtaskId}; skipping broadcast.",
                notification.CardId, notification.SubtaskId);
            return;
        }

        var payload = new SubtaskChangedNotification(
            Kind: PlanningChangeKind.Created,
            ProjectId: projectId.Value,
            CardId: notification.CardId,
            SubtaskId: notification.SubtaskId,
            OccurredAt: notification.OccurredAt);

        try
        {
            _logger.LogInformation(
                "Broadcasting SubtaskCreated to project:{ProjectId} (card {CardId}, subtask {SubtaskId}).",
                projectId.Value, notification.CardId, notification.SubtaskId);

            await _hub.Clients
                .Group($"project:{projectId.Value}")
                .SubtaskChanged(payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to broadcast SubtaskCreated to project:{ProjectId} (card {CardId}, subtask {SubtaskId}); persistence is unaffected.",
                projectId.Value, notification.CardId, notification.SubtaskId);
        }
    }
}

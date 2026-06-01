using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Source.Features.ProjectKanban.Events;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;
using Source.Infrastructure;
using Source.Shared.Events;

namespace Source.Features.ProjectKanban.EventHandlers;

/// <summary>
/// Counterpart to <see cref="BroadcastSubtaskCreatedHandler"/> for the soft-
/// delete branch (<c>ProjectKanbanCardSubtask.MarkDeleted</c>). Broadcasts a
/// <see cref="SubtaskChangedNotification"/> with <see cref="PlanningChangeKind.Deleted"/>
/// so subscribers can remove the checklist row from the rendered card.
///
/// <para><c>IgnoreQueryFilters()</c> on the card lookup is load-bearing here:
/// when a card is soft-deleted, its subtasks raise <c>SubtaskDeleted</c> from
/// the cascade — at broadcast time the parent card row is filtered out of the
/// default query and the broadcast would silently drop without the override.</para>
/// </summary>
public class BroadcastSubtaskDeletedHandler : IEventHandler<SubtaskDeleted>
{
    private readonly IHubContext<PlanningHub, IPlanningClient> _hub;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<BroadcastSubtaskDeletedHandler> _logger;

    public BroadcastSubtaskDeletedHandler(
        IHubContext<PlanningHub, IPlanningClient> hub,
        ApplicationDbContext db,
        ILogger<BroadcastSubtaskDeletedHandler> logger)
    {
        _hub = hub;
        _db = db;
        _logger = logger;
    }

    public async Task Handle(SubtaskDeleted notification, CancellationToken cancellationToken)
    {
        var projectId = await _db.ProjectKanbanCards
            .IgnoreQueryFilters()
            .Where(c => c.Id == notification.CardId)
            .Select(c => (Guid?)c.ProjectId)
            .FirstOrDefaultAsync(cancellationToken);

        if (projectId is null)
        {
            _logger.LogDebug(
                "BroadcastSubtaskDeletedHandler: orphan subtask event — no card {CardId} found for subtask {SubtaskId}; skipping broadcast.",
                notification.CardId, notification.SubtaskId);
            return;
        }

        var payload = new SubtaskChangedNotification(
            Kind: PlanningChangeKind.Deleted,
            ProjectId: projectId.Value,
            CardId: notification.CardId,
            SubtaskId: notification.SubtaskId,
            OccurredAt: notification.OccurredAt);

        try
        {
            _logger.LogInformation(
                "Broadcasting SubtaskDeleted to project:{ProjectId} (card {CardId}, subtask {SubtaskId}).",
                projectId.Value, notification.CardId, notification.SubtaskId);

            await _hub.Clients
                .Group($"project:{projectId.Value}")
                .SubtaskChanged(payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to broadcast SubtaskDeleted to project:{ProjectId} (card {CardId}, subtask {SubtaskId}); persistence is unaffected.",
                projectId.Value, notification.CardId, notification.SubtaskId);
        }
    }
}

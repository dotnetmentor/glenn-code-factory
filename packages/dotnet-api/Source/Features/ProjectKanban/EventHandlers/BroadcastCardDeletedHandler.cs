using Microsoft.AspNetCore.SignalR;
using Source.Features.ProjectKanban.Events;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;
using Source.Shared.Events;

namespace Source.Features.ProjectKanban.EventHandlers;

/// <summary>
/// Counterpart to <see cref="BroadcastCardCreatedHandler"/> for the soft-
/// delete branch (<c>ProjectKanbanCard.MarkDeleted</c>). Broadcasts a
/// <see cref="CardChangedNotification"/> with <see cref="PlanningChangeKind.Deleted"/>
/// so subscribers can drop the card from the rendered board.
/// </summary>
public class BroadcastCardDeletedHandler : IEventHandler<CardDeleted>
{
    private readonly IHubContext<PlanningHub, IPlanningClient> _hub;
    private readonly ILogger<BroadcastCardDeletedHandler> _logger;

    public BroadcastCardDeletedHandler(
        IHubContext<PlanningHub, IPlanningClient> hub,
        ILogger<BroadcastCardDeletedHandler> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public async Task Handle(CardDeleted notification, CancellationToken cancellationToken)
    {
        var payload = new CardChangedNotification(
            Kind: PlanningChangeKind.Deleted,
            ProjectId: notification.ProjectId,
            CardId: notification.CardId,
            OccurredAt: notification.OccurredAt);

        try
        {
            _logger.LogInformation(
                "Broadcasting CardDeleted to project:{ProjectId} (card {CardId}).",
                notification.ProjectId, notification.CardId);

            await _hub.Clients
                .Group($"project:{notification.ProjectId}")
                .CardChanged(payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to broadcast CardDeleted to project:{ProjectId} (card {CardId}); persistence is unaffected.",
                notification.ProjectId, notification.CardId);
        }
    }
}

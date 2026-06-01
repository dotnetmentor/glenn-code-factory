using Microsoft.AspNetCore.SignalR;
using Source.Features.ProjectKanban.Events;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;
using Source.Shared.Events;

namespace Source.Features.ProjectKanban.EventHandlers;

/// <summary>
/// Counterpart to <see cref="BroadcastCardCreatedHandler"/> for the
/// "column / position change" path (<c>ProjectKanbanCard.Move</c>). Broadcasts
/// a <see cref="CardChangedNotification"/> with <see cref="PlanningChangeKind.Moved"/>
/// — distinct from <c>Updated</c> so the UI can animate column transitions
/// differently from in-place metadata edits.
/// </summary>
public class BroadcastCardMovedHandler : IEventHandler<CardMoved>
{
    private readonly IHubContext<PlanningHub, IPlanningClient> _hub;
    private readonly ILogger<BroadcastCardMovedHandler> _logger;

    public BroadcastCardMovedHandler(
        IHubContext<PlanningHub, IPlanningClient> hub,
        ILogger<BroadcastCardMovedHandler> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public async Task Handle(CardMoved notification, CancellationToken cancellationToken)
    {
        var payload = new CardChangedNotification(
            Kind: PlanningChangeKind.Moved,
            ProjectId: notification.ProjectId,
            CardId: notification.CardId,
            OccurredAt: notification.OccurredAt);

        try
        {
            _logger.LogInformation(
                "Broadcasting CardMoved to project:{ProjectId} (card {CardId}, {OldStatus}@{OldPosition} -> {NewStatus}@{NewPosition}).",
                notification.ProjectId,
                notification.CardId,
                notification.OldStatus,
                notification.OldPosition,
                notification.NewStatus,
                notification.NewPosition);

            await _hub.Clients
                .Group($"project:{notification.ProjectId}")
                .CardChanged(payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to broadcast CardMoved to project:{ProjectId} (card {CardId}); persistence is unaffected.",
                notification.ProjectId, notification.CardId);
        }
    }
}

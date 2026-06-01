using Microsoft.AspNetCore.SignalR;
using Source.Features.ProjectKanban.Events;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;
using Source.Shared.Events;

namespace Source.Features.ProjectKanban.EventHandlers;

/// <summary>
/// Bridges the domain layer to connected React clients for kanban-card
/// creation: every <see cref="CardCreated"/> the interceptor dispatches fans
/// out to the <c>project:{ProjectId}</c> SignalR group as a
/// <see cref="CardChangedNotification"/> with <see cref="PlanningChangeKind.Created"/>.
///
/// <para>Mirrors <c>BroadcastAgentEventHandler</c> — same DI shape, same
/// exception-swallowing reliability contract. Persistence is independent of
/// the broadcast; a SignalR fault here must not poison the dispatcher chain.</para>
/// </summary>
public class BroadcastCardCreatedHandler : IEventHandler<CardCreated>
{
    private readonly IHubContext<PlanningHub, IPlanningClient> _hub;
    private readonly ILogger<BroadcastCardCreatedHandler> _logger;

    public BroadcastCardCreatedHandler(
        IHubContext<PlanningHub, IPlanningClient> hub,
        ILogger<BroadcastCardCreatedHandler> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public async Task Handle(CardCreated notification, CancellationToken cancellationToken)
    {
        var payload = new CardChangedNotification(
            Kind: PlanningChangeKind.Created,
            ProjectId: notification.ProjectId,
            CardId: notification.CardId,
            OccurredAt: notification.OccurredAt,
            Source: notification.Source,
            CreatedOnBranch: notification.CreatedOnBranch);

        try
        {
            _logger.LogInformation(
                "Broadcasting CardCreated to project:{ProjectId} (card {CardId}).",
                notification.ProjectId, notification.CardId);

            await _hub.Clients
                .Group($"project:{notification.ProjectId}")
                .CardChanged(payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to broadcast CardCreated to project:{ProjectId} (card {CardId}); persistence is unaffected.",
                notification.ProjectId, notification.CardId);
        }
    }
}

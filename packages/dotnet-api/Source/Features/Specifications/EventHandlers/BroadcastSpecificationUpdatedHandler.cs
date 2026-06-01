using Microsoft.AspNetCore.SignalR;
using Source.Features.Specifications.Events;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;
using Source.Shared.Events;

namespace Source.Features.Specifications.EventHandlers;

/// <summary>
/// Counterpart to <see cref="BroadcastSpecificationCreatedHandler"/> for the
/// "upsert hits an existing row" branch of <c>SaveSpecificationCommand</c>.
/// Broadcasts a <see cref="SpecificationChangedNotification"/> with
/// <see cref="PlanningChangeKind.Updated"/>.
/// </summary>
public class BroadcastSpecificationUpdatedHandler : IEventHandler<SpecificationUpdated>
{
    private readonly IHubContext<PlanningHub, IPlanningClient> _hub;
    private readonly ILogger<BroadcastSpecificationUpdatedHandler> _logger;

    public BroadcastSpecificationUpdatedHandler(
        IHubContext<PlanningHub, IPlanningClient> hub,
        ILogger<BroadcastSpecificationUpdatedHandler> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public async Task Handle(SpecificationUpdated notification, CancellationToken cancellationToken)
    {
        var payload = new SpecificationChangedNotification(
            Kind: PlanningChangeKind.Updated,
            ProjectId: notification.ProjectId,
            SpecificationId: notification.SpecificationId,
            Slug: notification.Slug,
            OccurredAt: notification.OccurredAt);

        try
        {
            _logger.LogInformation(
                "Broadcasting SpecificationUpdated to project:{ProjectId} (spec {SpecificationId}, slug {Slug}).",
                notification.ProjectId, notification.SpecificationId, notification.Slug);

            await _hub.Clients
                .Group($"project:{notification.ProjectId}")
                .SpecificationChanged(payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to broadcast SpecificationUpdated to project:{ProjectId} (spec {SpecificationId}); persistence is unaffected.",
                notification.ProjectId, notification.SpecificationId);
        }
    }
}

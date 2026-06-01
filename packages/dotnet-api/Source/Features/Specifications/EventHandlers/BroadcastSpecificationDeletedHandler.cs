using Microsoft.AspNetCore.SignalR;
using Source.Features.Specifications.Events;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;
using Source.Shared.Events;

namespace Source.Features.Specifications.EventHandlers;

/// <summary>
/// Counterpart to <see cref="BroadcastSpecificationCreatedHandler"/> for the
/// soft-delete branch (<c>Specification.MarkDeleted</c>). Broadcasts a
/// <see cref="SpecificationChangedNotification"/> with
/// <see cref="PlanningChangeKind.Deleted"/> so subscribers can drop the row
/// from the rendered spec list.
/// </summary>
public class BroadcastSpecificationDeletedHandler : IEventHandler<SpecificationDeleted>
{
    private readonly IHubContext<PlanningHub, IPlanningClient> _hub;
    private readonly ILogger<BroadcastSpecificationDeletedHandler> _logger;

    public BroadcastSpecificationDeletedHandler(
        IHubContext<PlanningHub, IPlanningClient> hub,
        ILogger<BroadcastSpecificationDeletedHandler> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public async Task Handle(SpecificationDeleted notification, CancellationToken cancellationToken)
    {
        var payload = new SpecificationChangedNotification(
            Kind: PlanningChangeKind.Deleted,
            ProjectId: notification.ProjectId,
            SpecificationId: notification.SpecificationId,
            Slug: notification.Slug,
            OccurredAt: notification.OccurredAt);

        try
        {
            _logger.LogInformation(
                "Broadcasting SpecificationDeleted to project:{ProjectId} (spec {SpecificationId}, slug {Slug}).",
                notification.ProjectId, notification.SpecificationId, notification.Slug);

            await _hub.Clients
                .Group($"project:{notification.ProjectId}")
                .SpecificationChanged(payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to broadcast SpecificationDeleted to project:{ProjectId} (spec {SpecificationId}); persistence is unaffected.",
                notification.ProjectId, notification.SpecificationId);
        }
    }
}

using Microsoft.AspNetCore.SignalR;
using Source.Features.Specifications.Events;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;
using Source.Shared.Events;

namespace Source.Features.Specifications.EventHandlers;

/// <summary>
/// Bridges the domain layer to connected React clients for specification
/// creation: every <see cref="SpecificationCreated"/> the interceptor dispatches
/// fans out to the <c>project:{ProjectId}</c> SignalR group as a
/// <see cref="SpecificationChangedNotification"/> with
/// <see cref="PlanningChangeKind.Created"/>.
///
/// <para>Mirrors <c>BroadcastAgentEventHandler</c> exactly — same DI shape,
/// same group convention, same exception-swallowing reliability contract.
/// The persistence side of the event was already committed inside the same
/// <c>SaveChanges</c> that scheduled this handler; a SignalR fault here must
/// not poison the rest of the dispatcher chain. Worst case clients miss a
/// live tick and refetch via the spec REST endpoint.</para>
/// </summary>
public class BroadcastSpecificationCreatedHandler : IEventHandler<SpecificationCreated>
{
    private readonly IHubContext<PlanningHub, IPlanningClient> _hub;
    private readonly ILogger<BroadcastSpecificationCreatedHandler> _logger;

    public BroadcastSpecificationCreatedHandler(
        IHubContext<PlanningHub, IPlanningClient> hub,
        ILogger<BroadcastSpecificationCreatedHandler> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public async Task Handle(SpecificationCreated notification, CancellationToken cancellationToken)
    {
        var payload = new SpecificationChangedNotification(
            Kind: PlanningChangeKind.Created,
            ProjectId: notification.ProjectId,
            SpecificationId: notification.SpecificationId,
            Slug: notification.Slug,
            OccurredAt: notification.OccurredAt);

        try
        {
            _logger.LogInformation(
                "Broadcasting SpecificationCreated to project:{ProjectId} (spec {SpecificationId}, slug {Slug}).",
                notification.ProjectId, notification.SpecificationId, notification.Slug);

            await _hub.Clients
                .Group($"project:{notification.ProjectId}")
                .SpecificationChanged(payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to broadcast SpecificationCreated to project:{ProjectId} (spec {SpecificationId}); persistence is unaffected.",
                notification.ProjectId, notification.SpecificationId);
        }
    }
}

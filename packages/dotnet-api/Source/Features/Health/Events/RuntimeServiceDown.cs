using Source.Shared.Events;

namespace Source.Features.Health.Events;

/// <summary>
/// Raised by the heartbeat handler's service-down detector when a runtime's
/// declared spec lists a supervised service that the daemon's heartbeat says
/// is NOT currently RUNNING. The
/// <see cref="EventHandlers.DispatchRestartServiceHandler"/> reacts by pushing
/// a <see cref="Contracts.RestartServicePayload"/> down the
/// <c>runtime-{RuntimeId}</c> SignalR group so the daemon's
/// <c>restart_service</c> tool can attempt recovery without a user prompt.
///
/// <para><b>Not an entity event.</b> This is a transient detection — there's
/// no <see cref="ProjectRuntime"/> field that flipped to carry it. Published
/// directly via <see cref="MediatR.IPublisher"/> from the hub, not through
/// the <c>DomainEventInterceptor</c>. We deliberately don't archive it to
/// <c>StoredDomainEvents</c>: a service flapping at heartbeat cadence would
/// burst-fill the table with rows that the throttle in the dispatch handler
/// already reduces to actionable signal.</para>
///
/// <para><see cref="DetectedAt"/> is the server clock at receive time —
/// daemon clocks could be skewed; the detector uses <see cref="IClock"/>
/// (passed through from the hub) so tests can pin time deterministically.</para>
/// </summary>
public record RuntimeServiceDown(
    Guid RuntimeId,
    string ServiceName,
    DateTime DetectedAt) : IDomainEvent
{
    public DateTime OccurredAt => DetectedAt;
}

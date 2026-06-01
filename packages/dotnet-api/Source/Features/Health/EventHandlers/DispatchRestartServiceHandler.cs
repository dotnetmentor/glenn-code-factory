using Microsoft.AspNetCore.SignalR;
using Source.Features.Health.Events;
using Source.Features.Health.Services;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;
using Source.Shared;
using Source.Shared.Events;

namespace Source.Features.Health.EventHandlers;

/// <summary>
/// Reacts to <see cref="RuntimeServiceDown"/> by pushing a
/// <see cref="RestartServicePayload"/> down the <c>runtime-{RuntimeId}</c>
/// SignalR group so the daemon can attempt to bring the service back without
/// a user prompt. The .NET <see cref="IRuntimeClient.RestartService"/>
/// surface lands at the daemon's existing
/// <c>signalr.onRestartService</c> handler (already wired in
/// <c>packages/daemon/src/main.ts</c>) which delegates to the in-process
/// <c>restart_service</c> tool.
///
/// <para><b>Throttle.</b> <see cref="RestartServiceThrottle"/> caps dispatches
/// at <see cref="RestartServiceThrottle.MaxDispatches"/> per
/// <see cref="RestartServiceThrottle.Window"/> per
/// <c>(runtimeId, serviceName)</c>. Over-cap dispatches are dropped with a
/// warning so operators see the runaway in logs but the daemon doesn't get
/// machine-gunned. The throttle is a singleton — its outage-window state
/// must outlive the per-event scope.</para>
///
/// <para><b>Failures.</b> Hub broadcast errors are intentionally swallowed
/// (logged at warning). The detector will re-fire on a subsequent heartbeat
/// once the service comes back up and goes down again, and the daemon's
/// retry on its own (heartbeat-respawn) is a safety net beyond this push.</para>
/// </summary>
public class DispatchRestartServiceHandler : IEventHandler<RuntimeServiceDown>
{
    private readonly IHubContext<RuntimeHub, IRuntimeClient> _hub;
    private readonly RestartServiceThrottle _throttle;
    private readonly IClock _clock;
    private readonly ILogger<DispatchRestartServiceHandler> _logger;

    public DispatchRestartServiceHandler(
        IHubContext<RuntimeHub, IRuntimeClient> hub,
        RestartServiceThrottle throttle,
        IClock clock,
        ILogger<DispatchRestartServiceHandler> logger)
    {
        _hub = hub;
        _throttle = throttle;
        _clock = clock;
        _logger = logger;
    }

    public async Task Handle(RuntimeServiceDown notification, CancellationToken cancellationToken)
    {
        if (!_throttle.TryClaim(notification.RuntimeId, notification.ServiceName, _clock.UtcNow))
        {
            _logger.LogWarning(
                "RestartService dispatch throttled for runtime {RuntimeId} service {ServiceName}: cap of {MaxDispatches} per {WindowMinutes}min reached. Dropping this dispatch; manual operator intervention may be required.",
                notification.RuntimeId,
                notification.ServiceName,
                RestartServiceThrottle.MaxDispatches,
                RestartServiceThrottle.Window.TotalMinutes);
            return;
        }

        var requestId = Guid.NewGuid();
        var payload = new RestartServicePayload(
            RuntimeId: notification.RuntimeId,
            ServiceName: notification.ServiceName,
            Reason: "service_down_detected",
            RequestId: requestId);

        try
        {
            await _hub.Clients
                .Group($"runtime-{notification.RuntimeId}")
                .RestartService(payload);

            _logger.LogInformation(
                "RestartService dispatched: runtime {RuntimeId}, service {ServiceName}, requestId {RequestId}.",
                notification.RuntimeId,
                notification.ServiceName,
                requestId);
        }
        catch (Exception ex)
        {
            // Same swallow-and-warn pattern as BroadcastRuntimeStateChangedHandler.
            // The detector will re-fire next heartbeat if the service is still
            // down and the daemon's connection has recovered by then.
            _logger.LogWarning(ex,
                "Failed to dispatch RestartService to runtime {RuntimeId} for service {ServiceName} (requestId {RequestId}); will rely on detector re-fire next heartbeat.",
                notification.RuntimeId,
                notification.ServiceName,
                requestId);
        }
    }
}

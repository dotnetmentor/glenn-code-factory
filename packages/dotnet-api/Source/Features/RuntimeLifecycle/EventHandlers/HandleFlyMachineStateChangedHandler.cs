using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Source.Features.FlyManagement.Events;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;
using Source.Shared.Events;

namespace Source.Features.RuntimeLifecycle.EventHandlers;

/// <summary>
/// Translates Fly machine-state webhooks (delivered as <see cref="FlyMachineStateChanged"/>)
/// into <see cref="ProjectRuntime"/> lifecycle transitions. The
/// <c>FlyWebhookController</c> already verified the HMAC, deduplicated the
/// <c>FlyEventId</c> against the recent <c>FlyOperation</c> audit log, and persisted
/// the audit row before publishing the domain event — so this handler can assume
/// the payload is authentic and not a redelivery.
///
/// <para>The mapping table below is intentionally narrow: only the (Fly state,
/// current runtime state) pairs we actively expect map to a transition. Anything
/// else is logged at debug and ignored — Fly emits more states than our state
/// graph cares about, and accepting them blindly would cause illegal-transition
/// failures or orthogonal mutations.</para>
///
/// <para>Idempotency is upstream: if Fly redelivers the same event, the
/// controller short-circuits to a 200 ack without publishing the domain event,
/// so this handler doesn't need its own dedup.</para>
/// </summary>
public class HandleFlyMachineStateChangedHandler : IEventHandler<FlyMachineStateChanged>
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<HandleFlyMachineStateChangedHandler> _logger;

    public HandleFlyMachineStateChangedHandler(
        ApplicationDbContext db,
        ILogger<HandleFlyMachineStateChangedHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task Handle(FlyMachineStateChanged notification, CancellationToken cancellationToken)
    {
        var runtime = await _db.ProjectRuntimes
            .FirstOrDefaultAsync(r => r.FlyMachineId == notification.MachineId, cancellationToken);

        if (runtime is null)
        {
            _logger.LogDebug(
                "FlyMachineStateChanged for unknown machine {MachineId}, ignoring",
                notification.MachineId);
            return;
        }

        var flyState = (notification.NewState ?? string.Empty).ToLowerInvariant();
        var currentState = runtime.State;

        // Mapping table: (Fly state, current ProjectRuntime state) -> (target state, reason)
        (RuntimeState target, string reason)? mapping = (flyState, currentState) switch
        {
            ("started", RuntimeState.Booting) => (RuntimeState.Bootstrapping, "fly_webhook:machine.started"),
            // Daemon-as-downloadable: Waking + fly:started no longer means
            // "Online" — the daemon still has to download + verify the
            // bundle and report back via RuntimeReady. We hand off to
            // Bootstrapping; the Bootstrapping → Online edge is owned
            // exclusively by the daemon's hub call.
            ("started", RuntimeState.Waking) => (RuntimeState.Bootstrapping, "fly_webhook:machine.started_after_wake"),
            ("stopped", RuntimeState.Suspending) => (RuntimeState.Suspended, "fly_webhook:machine.stopped"),
            ("destroyed", RuntimeState.Suspending) => (RuntimeState.Suspended, "fly_webhook:machine.destroyed_during_suspend"),
            ("destroyed", RuntimeState.Deleting) => (RuntimeState.Deleted, "fly_webhook:machine.destroyed"),
            ("crashed", RuntimeState.Online) => (RuntimeState.Crashed, "fly_webhook:machine.crashed"),
            ("crashed", RuntimeState.Bootstrapping) => (RuntimeState.Crashed, "fly_webhook:machine.crashed"),
            ("crashed", RuntimeState.Booting) => (RuntimeState.Crashed, "fly_webhook:machine.crashed"),
            ("crashed", RuntimeState.Waking) => (RuntimeState.Crashed, "fly_webhook:machine.crashed"),
            _ => null,
        };

        if (mapping is null)
        {
            _logger.LogDebug(
                "FlyMachineStateChanged ignored: no mapping for fly_state={FlyState} current_state={CurrentState} machine={MachineId}",
                flyState,
                currentState,
                notification.MachineId);
            return;
        }

        var (target, reason) = mapping.Value;

        // Bump the respawn counter when transitioning into Crashed so the supervisor
        // can enforce the retry budget. Reset happens automatically inside
        // ProjectRuntime.TransitionTo when reaching Online.
        if (target == RuntimeState.Crashed)
        {
            runtime.RespawnRetries += 1;
        }

        var metadata = JsonSerializer.Serialize(new
        {
            flyEventId = notification.FlyEventId,
            flyState,
            flyPreviousState = notification.PreviousState ?? string.Empty,
        });

        var result = runtime.TransitionTo(target, reason, "fly:webhook", metadata);
        if (result.IsFailure)
        {
            _logger.LogWarning(
                "Illegal transition rejected: {RuntimeId} {From} -> {To}: {Error}",
                runtime.Id,
                currentState,
                target,
                result.Error);
            return;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }
}

using MediatR;
using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimeLifecycle.Commands;

/// <summary>
/// Daemon-initiated signal "I'm done bootstrapping; the runtime is ready to
/// accept turns." Routed via <see cref="SignalR.Hubs.RuntimeHub.RuntimeReady"/>
/// after the daemon's RuntimeToken-authenticated hub call has projected its
/// <c>rt_runtime</c> claim into <see cref="RuntimeId"/>.
///
/// <list type="bullet">
///   <item>Loads the runtime; missing / soft-deleted → Result.Failure (the
///         hub surfaces this as <c>HubException</c> so the daemon can retry).</item>
///   <item>State == <see cref="RuntimeState.Online"/> → idempotent no-op.
///         A daemon that crash-loops its own boot trampoline may double-call;
///         the second call must be a clean Success (no audit row, no event)
///         rather than an illegal-transition error.</item>
///   <item>State == <see cref="RuntimeState.Waking"/> → walk through
///         Bootstrapping first. The Waking → Bootstrapping edge is normally
///         driven by <c>RuntimeFlyDriftPoller</c> observing the Fly machine in
///         <c>started</c>, but that poll runs on a ~15s interval. After the
///         <c>BackgroundRepoFetcher</c> change (commit 94f97cd) the daemon's
///         RuntimeReady can arrive in ~3s — well ahead of the poll — and was
///         being rejected, forcing 5 retries + a fresh second bootstrap and
///         pushing wake-to-Online from ~3s to ~67s. Two
///         <see cref="ProjectRuntime.TransitionTo"/> calls in one save:
///         Waking → Bootstrapping (reason <c>"daemon:runtime_ready
///         (implied)"</c>), then Bootstrapping → Online. Both events fire on
///         SaveChanges so the audit log still shows the canonical Waking →
///         Bootstrapping → Online journey, just driven by the daemon's
///         authoritative signal rather than the lagging poller.</item>
///   <item>State == <see cref="RuntimeState.Booting"/> or
///         <see cref="RuntimeState.Bootstrapping"/> → call the rich entity
///         method <see cref="ProjectRuntime.TransitionTo"/> with
///         <c>reason = "daemon:runtime_ready"</c> and <c>triggeredBy = "daemon"</c>.
///         Booting is included because the reconciler that flips Booting →
///         Bootstrapping runs on a poll interval and can lag the daemon by
///         tens of seconds; the daemon's runtime_ready broadcast is the
///         authoritative "I'm done bootstrapping" signal regardless of whether
///         the reconciler has caught up. Reasons match the runtime-lifecycle
///         spec's wording so audit-log readers see consistent strings. The
///         interceptor handles the event dispatch + audit row.</item>
///   <item>Any other state → Result.Failure with the offending state in the
///         message. The state machine itself would also reject the move, but
///         we want a clearer diagnostic ("daemon claimed ready while runtime
///         was Suspended") than the generic "Illegal transition" string.</item>
/// </list>
///
/// <para>No fan-out is needed here — <see cref="ProjectRuntime.TransitionTo"/>
/// raises <c>RuntimeStateChanged</c>, the interceptor publishes after
/// SaveChanges, and the existing
/// <c>BroadcastRuntimeStateChangedHandler</c> pushes the new state to the
/// project group. The command stays thin.</para>
/// </summary>
public record RuntimeReadyCommand(Guid RuntimeId) : ICommand<Result<Unit>>;

public class RuntimeReadyCommandHandler : ICommandHandler<RuntimeReadyCommand, Result<Unit>>
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<RuntimeReadyCommandHandler> _logger;

    public RuntimeReadyCommandHandler(
        ApplicationDbContext db,
        ILogger<RuntimeReadyCommandHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Result<Unit>> Handle(
        RuntimeReadyCommand request,
        CancellationToken cancellationToken)
    {
        // Tracked read — the rich-entity method mutates State and raises an
        // event, so we need the entity attached for the change tracker to pick
        // it up.
        var runtime = await _db.ProjectRuntimes
            .FirstOrDefaultAsync(r => r.Id == request.RuntimeId, cancellationToken);
        if (runtime is null)
        {
            // Soft-deleted runtimes are filtered by the global query filter,
            // so a janitor-marked or hard-deleted runtime simply won't be
            // found. The hub surfaces this as HubException; daemon can decide.
            return Result.Failure<Unit>(
                $"Runtime {request.RuntimeId} not found or soft-deleted.");
        }

        // Idempotent no-op. A daemon that retries its ready-ack across a
        // server restart, a boot-loop self-restart, or a transient hub
        // disconnect must not see "Illegal transition: Online -> Online" — the
        // double call is benign.
        if (runtime.State == RuntimeState.Online)
        {
            _logger.LogInformation(
                "RuntimeReady: runtime {RuntimeId} is already Online; treating as idempotent no-op.",
                runtime.Id);
            return Result.Success(Unit.Value);
        }

        // Waking → walk through Bootstrapping first. The daemon's RuntimeReady
        // call IS the authoritative "bootstrapping is done" signal — the
        // Waking → Bootstrapping edge is normally driven by RuntimeFlyDriftPoller
        // observing the Fly machine in `started`, but that poll runs on a ~15s
        // interval. After the BackgroundRepoFetcher change (commit 94f97cd) the
        // daemon's RuntimeReady can arrive in ~3s, well ahead of the poll, and
        // was being rejected — forcing 5 retries plus a fresh second bootstrap
        // (~64s of dead time before the runtime came Online). Issuing both edges
        // here gives the audit log the canonical Waking → Bootstrapping → Online
        // journey while collapsing the wall-clock latency to whatever the daemon
        // actually took to bootstrap.
        if (runtime.State == RuntimeState.Waking)
        {
            var implied = runtime.TransitionTo(
                RuntimeState.Bootstrapping,
                reason: "daemon:runtime_ready (implied)",
                triggeredBy: "daemon");
            if (!implied.IsSuccess)
            {
                return Result.Failure<Unit>(implied.Error!);
            }
        }

        // Defensive guard with a clearer message than the state-machine's
        // generic "Illegal transition: ..." string. Both Booting and
        // Bootstrapping are legal predecessors for Online: the daemon's
        // runtime_ready broadcast is authoritative, and may arrive before the
        // reconciler has flipped Booting → Bootstrapping. Waking was handled
        // above by walking it through Bootstrapping first.
        if (runtime.State != RuntimeState.Booting && runtime.State != RuntimeState.Bootstrapping)
        {
            return Result.Failure<Unit>(
                $"RuntimeReady refused: runtime {runtime.Id} is in state {runtime.State}, only Booting, Bootstrapping, Waking or Online are valid.");
        }

        var fromState = runtime.State;
        var transition = runtime.TransitionTo(
            RuntimeState.Online,
            reason: "daemon:runtime_ready",
            triggeredBy: "daemon");
        if (!transition.IsSuccess)
        {
            // The state-machine's own check tripped — only happens if
            // RuntimeStateMachine has been edited to drop Booting/Bootstrapping → Online.
            // Surface the underlying reason so the daemon's diagnostics are usable.
            return Result.Failure<Unit>(transition.Error!);
        }

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "RuntimeReady: runtime {RuntimeId} transitioned {FromState} -> Online.",
            runtime.Id,
            fromState);

        return Result.Success(Unit.Value);
    }
}

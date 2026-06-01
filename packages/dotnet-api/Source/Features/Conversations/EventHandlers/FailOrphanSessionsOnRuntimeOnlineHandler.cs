using Microsoft.EntityFrameworkCore;
using Source.Features.Conversations.Models;
using Source.Features.RuntimeLifecycle.Events;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;
using Source.Shared.Events;

namespace Source.Features.Conversations.EventHandlers;

/// <summary>
/// Reaps stale <see cref="AgentSession"/> rows when a <see cref="ProjectRuntime"/>
/// transitions <see cref="RuntimeState.Booting"/> / <see cref="RuntimeState.Bootstrapping"/>
/// → <see cref="RuntimeState.Online"/>. A freshly-bootstrapped daemon has no
/// in-memory knowledge of any prior session that was <see cref="AgentSessionStatus.Running"/>
/// or <see cref="AgentSessionStatus.Canceling"/> when its predecessor died, so
/// such sessions are by definition orphans the moment the new daemon promotes
/// to Online — the <c>turn_completed</c> / <c>turn_canceled</c> event that
/// would normally close them is never coming.
///
/// <list type="bullet">
///   <item><b>Trigger</b>: only on <see cref="RuntimeStateChanged.ToState"/>
///         equal to <see cref="RuntimeState.Online"/>, and only when
///         <see cref="RuntimeStateChanged.FromState"/> is Booting or
///         Bootstrapping. Every other transition into Online (today: none —
///         Booting/Bootstrapping are the only legal predecessors) short-
///         circuits as a safety net.</item>
///   <item><b>Pick</b>: every session bound to this runtime in Running or
///         Canceling, BUT only those whose <c>CreatedAt</c> predates the
///         runtime's last <c>FromState=Online</c> transition (i.e. the start
///         of the current respawn cycle). Sessions created after that
///         watershed were queued during downtime and belong to the new
///         daemon — they must not be reaped. The composite index on
///         <c>(RuntimeId, Status, QueuePosition)</c> keeps the scan cheap.</item>
///   <item><b>Reap</b>: call the rich-entity <see cref="AgentSession.Fail(string?)"/>
///         method with reason <c>"runtime_respawned"</c>. <c>Fail</c> is
///         idempotent on already-terminal rows, so a race with the orphan
///         janitor (which uses <c>"runtime_unavailable"</c>) is benign.</item>
///   <item><b>Fan-out</b>: each <c>Fail</c> raises
///         <see cref="Events.AgentSessionTerminated"/>, which fans through
///         <see cref="DispatchNextSessionHandler"/> to drain the per-runtime
///         queue — same chain the daemon's normal turn-completed path uses.
///         <see cref="DispatchQueuedSessionsOnRuntimeOnlineHandler"/> also
///         fires on this same RuntimeStateChanged event; either handler order
///         converges on the same outcome (queue gets dispatched after the
///         orphan is reaped).</item>
/// </list>
///
/// <para><b>Why not extend the orphan janitor.</b> The janitor scans periodically
/// based on the runtime's <i>current</i> state (Crashed/Failed/Suspended/…). A
/// respawn flow goes Crashed → Booting → Bootstrapping → Online quickly
/// (typically ~30s), and the minutely janitor often misses the Crashed window
/// entirely — by the time it fires, the runtime is already back to Online and
/// the janitor concludes "all good", leaving the orphaned session stuck in
/// Running forever. This handler closes that hole by reacting to the edge
/// (Booting/Bootstrapping → Online) instead of polling on the steady state.
/// The janitor stays in place as a backstop for the other unavailable
/// states.</para>
///
/// <para><b>Concurrent new submissions are safe (watershed filter).</b> Both
/// this handler and <see cref="DispatchQueuedSessionsOnRuntimeOnlineHandler"/>
/// fire on the same <see cref="RuntimeStateChanged"/> event and MediatR does
/// not guarantee their order. If the dispatch handler runs first it flips
/// Pending → Running for any queued head-of-queue prompt, which would then
/// appear as a Running candidate to us. To avoid wrongly reaping that brand-
/// new session, we compare each candidate's <c>CreatedAt</c> against the
/// timestamp the runtime LAST left Online (the start of the current respawn
/// cycle, looked up from <see cref="RuntimeStateEvent"/>). Sessions younger
/// than that watershed were queued during downtime, are bound to the new
/// daemon, and are excluded. On a runtime's first-ever Online transition the
/// watershed is null and we skip the reap entirely — no prior daemon, no
/// possible orphans. This closes the race that previously marked a fresh
/// first-boot prompt as <c>runtime_respawned</c> the instant the daemon
/// finished bootstrapping.</para>
/// </summary>
public class FailOrphanSessionsOnRuntimeOnlineHandler : IEventHandler<RuntimeStateChanged>
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<FailOrphanSessionsOnRuntimeOnlineHandler> _logger;

    public FailOrphanSessionsOnRuntimeOnlineHandler(
        ApplicationDbContext db,
        ILogger<FailOrphanSessionsOnRuntimeOnlineHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task Handle(RuntimeStateChanged notification, CancellationToken cancellationToken)
    {
        // Only react to transitions into Online from a fresh-bootstrap path.
        // Booting → Online happens when the daemon's runtime_ready broadcast
        // beats the reconciler's Booting → Bootstrapping poll; Bootstrapping →
        // Online is the steady-state happy path. Suspended/Waking → Online is
        // a different scenario (no daemon respawn, no orphan sessions to worry
        // about — the orphan janitor would have already reaped any
        // Running/Canceling sessions on a Suspended runtime).
        if (notification.ToState != RuntimeState.Online)
        {
            return;
        }

        if (notification.FromState != RuntimeState.Booting
            && notification.FromState != RuntimeState.Bootstrapping)
        {
            // Online from any other state means no fresh daemon, no orphan
            // hazard. (Today only Booting/Bootstrapping → Online is even
            // legal per the state machine, but the filter keeps the intent
            // explicit if the state graph grows.)
            return;
        }

        // Look up the moment the runtime LAST left Online — the start of the
        // current respawn cycle. This is the watershed:
        //
        //   - If null: the runtime has never been Online before. This is the
        //     first boot ever; there is no prior daemon and therefore no
        //     possible orphans. Skip the reap entirely — anything currently
        //     in Running on this runtime is a freshly-dispatched first-prompt
        //     session that DispatchQueuedSessionsOnRuntimeOnlineHandler just
        //     flipped Pending → Running on the same RuntimeStateChanged event.
        //     (The two handlers race on the same event; without this guard
        //     we wrongly reap the brand-new session as "runtime_respawned"
        //     even though the brand-new daemon is about to execute it.)
        //
        //   - If non-null: filter orphan candidates to sessions whose
        //     CreatedAt predates this watershed. Anything created AFTER the
        //     runtime last left Online was a fresh user prompt queued during
        //     downtime — it was Pending+queued and the dispatch handler
        //     legitimately flipped it to Running just now. Such sessions are
        //     bound to the NEW daemon and must NOT be reaped.
        //
        // The composite index (RuntimeId, CreatedAt DESC) on RuntimeStateEvents
        // makes this a single index seek.
        var lastLeftOnlineAt = await _db.RuntimeStateEvents
            .Where(e => e.RuntimeId == notification.RuntimeId
                     && e.FromState == RuntimeState.Online)
            .OrderByDescending(e => e.CreatedAt)
            .Select(e => (DateTime?)e.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (lastLeftOnlineAt is null)
        {
            _logger.LogDebug(
                "FailOrphanSessionsOnRuntimeOnlineHandler: runtime {RuntimeId} first-boot {FromState} -> Online; no prior daemon existed so no orphans are possible. Skipping reap.",
                notification.RuntimeId, notification.FromState);
            return;
        }

        // Find any session that the old daemon thought was in-flight. The new
        // daemon has no record of these — they will never see a daemon-driven
        // terminal event, so we close them here. Filter to sessions created
        // BEFORE the runtime last left Online; anything younger than that
        // watershed was queued during downtime and belongs to the new daemon.
        var orphans = await _db.AgentSessions
            .Where(s => s.RuntimeId == notification.RuntimeId
                     && (s.Status == AgentSessionStatus.Running
                         || s.Status == AgentSessionStatus.Canceling)
                     && s.CreatedAt < lastLeftOnlineAt.Value)
            .ToListAsync(cancellationToken);

        if (orphans.Count == 0)
        {
            _logger.LogDebug(
                "FailOrphanSessionsOnRuntimeOnlineHandler: runtime {RuntimeId} transitioned {FromState} -> Online with no pre-respawn in-flight sessions to reap (watershed={LastLeftOnlineAt:o}).",
                notification.RuntimeId, notification.FromState, lastLeftOnlineAt.Value);
            return;
        }

        foreach (var session in orphans)
        {
            // Rich-entity transition: Fail() is idempotent on already-terminal
            // rows, so a race against the orphan janitor (which uses
            // "runtime_unavailable") is benign — whichever path lands first
            // wins, the other is a no-op.
            session.Fail("runtime_respawned");
        }

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogWarning(
            "FailOrphanSessionsOnRuntimeOnlineHandler: reaped {Count} in-flight session(s) on runtime {RuntimeId} after {FromState} -> Online (reason=runtime_respawned, watershed={LastLeftOnlineAt:o})",
            orphans.Count, notification.RuntimeId, notification.FromState, lastLeftOnlineAt.Value);
    }
}

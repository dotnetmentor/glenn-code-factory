using Hangfire;
using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeLifecycle.Events;
using Source.Features.RuntimeLifecycle.Jobs;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;
using Source.Shared.Events;

namespace Source.Features.RuntimeLifecycle.EventHandlers;

/// <summary>
/// Reacts to <see cref="RuntimeStateChanged"/> when the new state is
/// <see cref="RuntimeState.Crashed"/> and decides what to do next:
/// <list type="bullet">
///   <item>If the runtime has already crashed <see cref="EscalationThreshold"/>
///         times within the last <see cref="EscalationWindow"/>, escalate to
///         <see cref="RuntimeState.Failed"/> — the supervisor gives up and an
///         operator must intervene.</item>
///   <item>Otherwise schedule a delayed <see cref="RespawnRuntimeJob"/> with a
///         retries-aware backoff (3 s → 30 s → 5 min) so a single transient
///         hiccup recovers fast and a persistent failure backs off.</item>
/// </list>
///
/// <para><b>Handler ordering / SaveChanges decision.</b> MediatR's default
/// <c>ForeachAwaitPublisher</c> dispatches notification handlers sequentially.
/// MediatR registers handlers in arbitrary (reflection) order, so we cannot
/// assume <see cref="PersistRuntimeStateEventHandler"/> ran first. However that
/// handler — and every other in-tree path that produces a <c>Crashed</c>
/// transition (the heartbeat watcher, the Fly webhook handler, the
/// <c>force-respawn</c> admin command in a follow-up card) — calls
/// <c>SaveChangesAsync</c> on its own DbContext. By the time the
/// <see cref="DomainEventInterceptor"/> reaches its <c>SavedChangesAsync</c>
/// hook (which is what fires this handler), the audit row corresponding to the
/// <i>current</i> crash is already committed, so a fresh
/// <see cref="RuntimeStateEvent"/> query sees it as part of <c>recentCrashes</c>.
/// We therefore compare <c>recentCrashes &gt;= EscalationThreshold</c> directly
/// without an off-by-one fudge.</para>
///
/// <para><b>Why we re-load the runtime.</b> The interceptor's
/// <c>SavedChangesAsync</c> already committed the parent transaction, so the
/// in-memory entity tracked by the producing handler is no longer "live" from
/// our perspective — we may even be on a different DbContext scope. To escalate
/// we need to mutate the row and save again, which means a fresh load + save.</para>
/// </summary>
public class ScheduleRespawnHandler : IEventHandler<RuntimeStateChanged>
{
    /// <summary>
    /// Crashes within <see cref="EscalationWindow"/> required to give up and
    /// transition to <see cref="RuntimeState.Failed"/>. The current crash is
    /// included in this count (see class-level remarks).
    /// </summary>
    private const int EscalationThreshold = 3;

    /// <summary>
    /// Sliding window used to count recent crashes for the escalation policy.
    /// 1 hour gives a flapping runtime enough rope to declare itself broken
    /// while still forgiving a single bad day from a week ago.
    /// </summary>
    private static readonly TimeSpan EscalationWindow = TimeSpan.FromHours(1);

    private readonly ApplicationDbContext _db;
    private readonly IBackgroundJobClient _backgroundJobs;
    private readonly ILogger<ScheduleRespawnHandler> _logger;

    public ScheduleRespawnHandler(
        ApplicationDbContext db,
        IBackgroundJobClient backgroundJobs,
        ILogger<ScheduleRespawnHandler> logger)
    {
        _db = db;
        _backgroundJobs = backgroundJobs;
        _logger = logger;
    }

    public async Task Handle(RuntimeStateChanged notification, CancellationToken cancellationToken)
    {
        // Only react to transitions that landed in Crashed. Every other state
        // is irrelevant to this orchestrator.
        if (notification.ToState != RuntimeState.Crashed)
        {
            return;
        }

        var since = DateTime.UtcNow - EscalationWindow;
        var recentCrashes = await _db.RuntimeStateEvents
            .CountAsync(e => e.RuntimeId == notification.RuntimeId
                          && e.ToState == RuntimeState.Crashed
                          && e.CreatedAt >= since, cancellationToken);

        if (recentCrashes >= EscalationThreshold)
        {
            await EscalateToFailedAsync(notification.RuntimeId, recentCrashes, cancellationToken);
            return;
        }

        // Load the runtime fresh so we can read the canonical RespawnRetries
        // counter (the ProjectRuntime row was already saved by the producer).
        var runtime = await _db.ProjectRuntimes
            .FirstOrDefaultAsync(r => r.Id == notification.RuntimeId, cancellationToken);

        if (runtime is null)
        {
            _logger.LogInformation(
                "ScheduleRespawn: runtime {RuntimeId} no longer exists, nothing to schedule",
                notification.RuntimeId);
            return;
        }

        var delay = GetBackoff(runtime.RespawnRetries);
        _backgroundJobs.Schedule<RespawnRuntimeJob>(
            j => j.Run(notification.RuntimeId, CancellationToken.None),
            delay);

        _logger.LogInformation(
            "Scheduled respawn for runtime {RuntimeId} in {DelaySeconds}s (retries={Retries}, recentCrashes={RecentCrashes})",
            notification.RuntimeId,
            (int)delay.TotalSeconds,
            runtime.RespawnRetries,
            recentCrashes);
    }

    /// <summary>
    /// Escalation path: the runtime has crashed too many times within the
    /// escalation window. Mark it <see cref="RuntimeState.Failed"/> and stop
    /// the respawn cycle. Defensive checks guard against a parallel transition
    /// (operator delete, manual reset) racing us out of <c>Crashed</c>.
    /// </summary>
    private async Task EscalateToFailedAsync(Guid runtimeId, int recentCrashes, CancellationToken ct)
    {
        var runtime = await _db.ProjectRuntimes
            .FirstOrDefaultAsync(r => r.Id == runtimeId, ct);

        if (runtime is null)
        {
            _logger.LogInformation(
                "ScheduleRespawn: runtime {RuntimeId} disappeared before escalation, no-op",
                runtimeId);
            return;
        }

        if (runtime.State != RuntimeState.Crashed)
        {
            // Someone else moved this row out of Crashed (operator delete, etc.)
            // between the producing handler's save and ours. Don't double-mutate.
            _logger.LogInformation(
                "ScheduleRespawn: runtime {RuntimeId} no longer Crashed (now {State}), skipping escalation",
                runtimeId, runtime.State);
            return;
        }

        var transition = runtime.TransitionTo(
            RuntimeState.Failed,
            $"respawn:exhausted (count={recentCrashes} within {(int)EscalationWindow.TotalMinutes}min)",
            "watcher:respawn",
            metadata: null);

        if (transition.IsFailure)
        {
            _logger.LogWarning(
                "ScheduleRespawn: could not escalate runtime {RuntimeId} to Failed: {Error}",
                runtimeId, transition.Error);
            return;
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogWarning(
            "Runtime {RuntimeId} escalated to Failed after {Count} crashes within {WindowMinutes} minute(s)",
            runtimeId, recentCrashes, (int)EscalationWindow.TotalMinutes);
    }

    /// <summary>
    /// Backoff schedule indexed by the cumulative <c>RespawnRetries</c> counter
    /// on the runtime row. First crash recovers fast; persistent failures back
    /// off so we don't hammer Fly.
    /// </summary>
    private static TimeSpan GetBackoff(int retries) => retries switch
    {
        0 => TimeSpan.FromSeconds(3),
        1 => TimeSpan.FromSeconds(30),
        _ => TimeSpan.FromMinutes(5),
    };
}

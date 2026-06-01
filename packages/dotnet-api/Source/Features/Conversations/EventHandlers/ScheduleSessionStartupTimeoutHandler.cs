using Hangfire;
using Source.Features.Conversations.Events;
using Source.Features.Conversations.Jobs;
using Source.Shared.Events;

namespace Source.Features.Conversations.EventHandlers;

/// <summary>
/// Reacts to <see cref="SessionDispatched"/> by scheduling a single delayed
/// <see cref="SessionStartupTimeoutJob"/> run <see cref="SessionStartupTimeoutJob.Delay"/>
/// in the future. The job inspects the session on fire and either no-ops (the
/// daemon has been emitting events normally) or marks the session
/// <see cref="Source.Features.Conversations.Models.AgentSessionStatus.Failed"/>
/// with reason <c>sdk_no_response</c> when no audit row beyond the seed
/// <c>PromptReceived</c> has been written.
///
/// <para><b>Why hook <see cref="SessionDispatched"/> specifically.</b> Every
/// path that pushes <c>StartTurn</c> to a runtime goes through
/// <c>AgentSession.Dispatch()</c> first — the create-and-dispatch path in
/// <c>TurnDispatcher</c>, <c>SubmitUrgentPromptCommand</c>, the queue-drain
/// handlers (<c>DispatchNextSessionHandler</c>,
/// <c>DispatchQueuedSessionsOnRuntimeOnlineHandler</c>) — and that method
/// always raises this event. Hooking here means one handler covers all four
/// dispatch sites and both backends (Claude + OpenCode) by construction; no
/// matter which entry point delivered the prompt, the deadline timer arms.</para>
///
/// <para><b>Why not arm in <c>TurnDispatcher</c> inline.</b> Inline scheduling
/// would mean every dispatch site has to remember to call the same Schedule
/// helper — easy to miss when a new dispatch path is added (the OpenCode card
/// added one, and the urgent-prompt card added another). Hooking the existing
/// domain event keeps the policy centralised and impossible to forget.</para>
///
/// <para><b>Lightweight handler.</b> No DB read, no state inspection — just
/// a <c>Schedule(...)</c> call. The job itself does the work; this handler is
/// pure "arm the timer." Safe on the SaveChanges interceptor path.</para>
/// </summary>
public class ScheduleSessionStartupTimeoutHandler : IEventHandler<SessionDispatched>
{
    private readonly IBackgroundJobClient _backgroundJobs;
    private readonly ILogger<ScheduleSessionStartupTimeoutHandler> _logger;

    public ScheduleSessionStartupTimeoutHandler(
        IBackgroundJobClient backgroundJobs,
        ILogger<ScheduleSessionStartupTimeoutHandler> logger)
    {
        _backgroundJobs = backgroundJobs;
        _logger = logger;
    }

    public Task Handle(SessionDispatched notification, CancellationToken cancellationToken)
    {
        // Schedule a single delayed run keyed on the session id. The job is
        // idempotent (predicate + Fail() are both no-ops if the session has
        // already moved on) so re-scheduling on a hypothetical re-dispatch
        // would also be safe; today's lifecycle doesn't re-dispatch.
        _backgroundJobs.Schedule<SessionStartupTimeoutJob>(
            j => j.Run(notification.SessionId, CancellationToken.None),
            SessionStartupTimeoutJob.Delay);

        _logger.LogDebug(
            "Scheduled SessionStartupTimeout for session {SessionId} (runtime {RuntimeId}) in {DelaySeconds}s",
            notification.SessionId,
            notification.RuntimeId,
            (int)SessionStartupTimeoutJob.Delay.TotalSeconds);

        return Task.CompletedTask;
    }
}

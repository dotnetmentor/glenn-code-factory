using Source.Features.RuntimeLifecycle.Events;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;
using Source.Shared.Events;

namespace Source.Features.RuntimeLifecycle.EventHandlers;

/// <summary>
/// Reacts to <see cref="RuntimeStateChanged"/> by appending a
/// <see cref="RuntimeStateEvent"/> row — our permanent, FK-less audit trail
/// of every lifecycle transition.
///
/// <para>The <c>DomainEventInterceptor</c> dispatches events <i>after</i> the
/// source <c>SaveChangesAsync</c> commits, so this handler issues a separate
/// save. That's intentional and matches the established pattern: the audit
/// row reflects a transition that has already been persisted on
/// <see cref="ProjectRuntime"/>; nothing here can roll back the source change.</para>
/// </summary>
public class PersistRuntimeStateEventHandler : IEventHandler<RuntimeStateChanged>
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<PersistRuntimeStateEventHandler> _logger;

    public PersistRuntimeStateEventHandler(
        ApplicationDbContext context,
        ILogger<PersistRuntimeStateEventHandler> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task Handle(RuntimeStateChanged notification, CancellationToken cancellationToken)
    {
        var auditRow = new RuntimeStateEvent
        {
            Id = Guid.NewGuid(),
            RuntimeId = notification.RuntimeId,
            FromState = notification.FromState,
            ToState = notification.ToState,
            Reason = notification.Reason,
            TriggeredBy = notification.TriggeredBy,
            Metadata = notification.Metadata,
        };

        _context.RuntimeStateEvents.Add(auditRow);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Recorded runtime state transition {RuntimeId}: {FromState} -> {ToState} (reason={Reason}, by={TriggeredBy})",
            notification.RuntimeId,
            notification.FromState,
            notification.ToState,
            notification.Reason,
            notification.TriggeredBy);
    }
}

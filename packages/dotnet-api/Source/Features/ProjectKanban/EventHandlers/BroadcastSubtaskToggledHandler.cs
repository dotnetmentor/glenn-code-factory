using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Source.Features.ProjectKanban.Events;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;
using Source.Infrastructure;
using Source.Shared.Events;

namespace Source.Features.ProjectKanban.EventHandlers;

/// <summary>
/// Counterpart to <see cref="BroadcastSubtaskCreatedHandler"/> for the
/// <c>IsCompleted</c> flip path (<c>ProjectKanbanCardSubtask.Toggle</c>).
/// Broadcasts a <see cref="SubtaskChangedNotification"/> with
/// <see cref="PlanningChangeKind.Toggled"/>; the new <c>IsCompleted</c> value
/// is intentionally NOT on the wire (the React side re-fetches the subtask /
/// card on receipt), matching the payload-light convention.
///
/// <para>Looks up <c>ProjectId</c> via a cheap single-column projection on
/// the parent card, same as the Created handler. <c>IgnoreQueryFilters()</c>
/// covers the corner where a toggle race-fires after a soft-delete.</para>
/// </summary>
public class BroadcastSubtaskToggledHandler : IEventHandler<SubtaskToggled>
{
    private readonly IHubContext<PlanningHub, IPlanningClient> _hub;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<BroadcastSubtaskToggledHandler> _logger;

    public BroadcastSubtaskToggledHandler(
        IHubContext<PlanningHub, IPlanningClient> hub,
        ApplicationDbContext db,
        ILogger<BroadcastSubtaskToggledHandler> logger)
    {
        _hub = hub;
        _db = db;
        _logger = logger;
    }

    public async Task Handle(SubtaskToggled notification, CancellationToken cancellationToken)
    {
        var projectId = await _db.ProjectKanbanCards
            .IgnoreQueryFilters()
            .Where(c => c.Id == notification.CardId)
            .Select(c => (Guid?)c.ProjectId)
            .FirstOrDefaultAsync(cancellationToken);

        if (projectId is null)
        {
            _logger.LogDebug(
                "BroadcastSubtaskToggledHandler: orphan subtask event — no card {CardId} found for subtask {SubtaskId}; skipping broadcast.",
                notification.CardId, notification.SubtaskId);
            return;
        }

        var payload = new SubtaskChangedNotification(
            Kind: PlanningChangeKind.Toggled,
            ProjectId: projectId.Value,
            CardId: notification.CardId,
            SubtaskId: notification.SubtaskId,
            OccurredAt: notification.OccurredAt);

        try
        {
            _logger.LogInformation(
                "Broadcasting SubtaskToggled to project:{ProjectId} (card {CardId}, subtask {SubtaskId}, completed={IsCompleted}).",
                projectId.Value, notification.CardId, notification.SubtaskId, notification.IsCompleted);

            await _hub.Clients
                .Group($"project:{projectId.Value}")
                .SubtaskChanged(payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to broadcast SubtaskToggled to project:{ProjectId} (card {CardId}, subtask {SubtaskId}); persistence is unaffected.",
                projectId.Value, notification.CardId, notification.SubtaskId);
        }
    }
}

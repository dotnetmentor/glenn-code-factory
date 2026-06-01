using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Source.Features.Conversations.Events;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;
using Source.Infrastructure;
using Source.Shared.Events;

namespace Source.Features.Conversations.EventHandlers;

/// <summary>
/// Bridges the domain layer to connected React clients for conversation title
/// changes. Every time <see cref="Conversation.Rename"/> or
/// <see cref="Conversation.AutoRetitle"/> raises <see cref="ConversationRenamed"/>
/// the dispatcher invokes this handler, which fans the event out to the
/// <c>project-{id}</c> SignalR group as a
/// <see cref="ConversationRenamedNotification"/>.
///
/// <para>Mirrors <see cref="BroadcastAgentEventHandler"/> exactly — same DI
/// shape, same group convention, same exception-swallowing reliability
/// contract. The DB write that triggered the event already committed in the
/// same SaveChanges that scheduled this handler; a SignalR fault here must
/// not poison the rest of the dispatcher chain. Worst case other tabs miss
/// the live tick and pick up the new title on their next conversation refetch.</para>
/// </summary>
public class BroadcastConversationRenamedHandler : IEventHandler<ConversationRenamed>
{
    private readonly IHubContext<AgentHub, IAgentClient> _hub;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<BroadcastConversationRenamedHandler> _logger;

    public BroadcastConversationRenamedHandler(
        IHubContext<AgentHub, IAgentClient> hub,
        ApplicationDbContext db,
        ILogger<BroadcastConversationRenamedHandler> logger)
    {
        _hub = hub;
        _db = db;
        _logger = logger;
    }

    public async Task Handle(ConversationRenamed notification, CancellationToken cancellationToken)
    {
        var payload = new ConversationRenamedNotification(
            ConversationId: notification.ConversationId,
            ProjectId: notification.ProjectId,
            Title: notification.Title,
            IsAutoTitled: notification.IsAutoTitled,
            OccurredAt: notification.OccurredAt);

        try
        {
            await _hub.Clients
                .Group($"branch-{notification.BranchId}")
                .ConversationRenamed(payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to broadcast ConversationRenamed to branch group for conversation {ConversationId} (branch {BranchId}, project {ProjectId}); persistence is unaffected.",
                notification.ConversationId,
                notification.BranchId,
                notification.ProjectId);
        }

        // Additive workspace-group broadcast — lets the sidebar pick up
        // auto-retitles and explicit renames live even when the user is
        // currently focused on a different project's tab.
        try
        {
            var workspaceId = await _db.Projects
                .AsNoTracking()
                .Where(p => p.Id == notification.ProjectId)
                .Select(p => (Guid?)p.WorkspaceId)
                .FirstOrDefaultAsync(cancellationToken);
            if (workspaceId is { } wsId)
            {
                await _hub.Clients
                    .Group($"workspace-{wsId}")
                    .ConversationRenamed(payload);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to broadcast ConversationRenamed to workspace group for conversation {ConversationId} (project {ProjectId}); persistence is unaffected.",
                notification.ConversationId,
                notification.ProjectId);
        }
    }
}

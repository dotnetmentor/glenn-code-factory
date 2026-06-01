using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Source.Features.Attachments.Services;
using Source.Features.Conversations.Models;
using Source.Features.RuntimeLifecycle.Events;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;
using Source.Infrastructure;
using Source.Shared.Events;

namespace Source.Features.Conversations.EventHandlers;

public class DispatchQueuedSessionsOnRuntimeOnlineHandler : IEventHandler<RuntimeStateChanged>
{
    private readonly ApplicationDbContext _db;
    private readonly IHubContext<RuntimeHub, IRuntimeClient> _runtimeHub;
    private readonly ILogger<DispatchQueuedSessionsOnRuntimeOnlineHandler> _logger;

    public DispatchQueuedSessionsOnRuntimeOnlineHandler(
        ApplicationDbContext db,
        IHubContext<RuntimeHub, IRuntimeClient> runtimeHub,
        ILogger<DispatchQueuedSessionsOnRuntimeOnlineHandler> logger)
    {
        _db = db;
        _runtimeHub = runtimeHub;
        _logger = logger;
    }

    public async Task Handle(RuntimeStateChanged notification, CancellationToken cancellationToken)
    {
        if (notification.ToState != RuntimeState.Online)
        {
            return;
        }

        var hasActive = await _db.AgentSessions
            .AnyAsync(s => s.RuntimeId == notification.RuntimeId
                && (s.Status == AgentSessionStatus.Running
                    || s.Status == AgentSessionStatus.Canceling), cancellationToken);
        if (hasActive)
        {
            return;
        }

        var next = await _db.AgentSessions
            .Include(s => s.Model)
            .Include(s => s.Conversation)
            .Where(s => s.RuntimeId == notification.RuntimeId
                     && s.Status == AgentSessionStatus.Pending
                     && s.QueuePosition != null)
            .OrderBy(s => s.QueuePosition)
            .FirstOrDefaultAsync(cancellationToken);

        if (next is null)
        {
            return;
        }

        string? resolvedModelSlug = null;
        if (next.Model is { IsActive: true } sessionModel)
        {
            resolvedModelSlug = sessionModel.Slug;
        }
        else if (next.Conversation is { ProjectId: var projectId })
        {
            resolvedModelSlug = await _db.Projects
                .AsNoTracking()
                .Where(p => p.Id == projectId && p.Model != null && p.Model.IsActive)
                .Select(p => p.Model!.Slug)
                .FirstOrDefaultAsync(cancellationToken);
        }

        var pullBeforeStart = !await _db.AgentSessions
            .AnyAsync(s => s.RuntimeId == notification.RuntimeId && s.Id != next.Id, cancellationToken);

        // chat-file-attachments — re-load this queued session's attachments
        // (stamped with its SessionId at enqueue time) so the daemon-bound
        // prompt carries the per-file path block; stored Prompt stays raw.
        var attachments = await _db.Attachments
            .AsNoTracking()
            .Where(a => a.SessionId == next.Id)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(cancellationToken);

        var promptForAgent = PromptPrefixBuilder.BuildPromptWithAttachments(
            next.Prompt, attachments);

        var startPayload = new StartTurnPayload(
            SessionId: next.Id,
            ConversationId: next.ConversationId,
            Prompt: promptForAgent,
            Model: resolvedModelSlug,
            AgentId: next.AgentId,
            PullBeforeStart: pullBeforeStart);

        try
        {
            next.Dispatch();
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _logger.LogWarning(ex,
                "DispatchQueuedSessionsOnRuntimeOnlineHandler: concurrency conflict dispatching session {SessionId}.",
                next.Id);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "DispatchQueuedSessionsOnRuntimeOnlineHandler: unexpected failure dispatching session {SessionId}.",
                next.Id);
            try
            {
                next.Fail($"Failed to dispatch after runtime came online: {ex.Message}");
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception saveEx)
            {
                _logger.LogError(saveEx,
                    "DispatchQueuedSessionsOnRuntimeOnlineHandler: also failed to mark session {SessionId} Failed.",
                    next.Id);
            }
            return;
        }

        try
        {
            await _runtimeHub.Clients
                .Group($"runtime-{notification.RuntimeId}")
                .StartTurn(startPayload);

            _logger.LogInformation(
                "DispatchQueuedSessionsOnRuntimeOnlineHandler: dispatched queued session {SessionId} on runtime {RuntimeId}.",
                next.Id, notification.RuntimeId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "DispatchQueuedSessionsOnRuntimeOnlineHandler: failed to send StartTurn for session {SessionId}.",
                next.Id);
        }
    }
}

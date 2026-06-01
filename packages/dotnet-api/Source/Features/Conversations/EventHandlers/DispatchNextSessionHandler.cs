using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Source.Features.Attachments.Services;
using Source.Features.Conversations.Events;
using Source.Features.Conversations.Models;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;
using Source.Infrastructure;
using Source.Shared.Events;

namespace Source.Features.Conversations.EventHandlers;

public class DispatchNextSessionHandler : IEventHandler<AgentSessionTerminated>
{
    private readonly ApplicationDbContext _db;
    private readonly IHubContext<RuntimeHub, IRuntimeClient> _runtimeHub;
    private readonly ILogger<DispatchNextSessionHandler> _logger;

    public DispatchNextSessionHandler(
        ApplicationDbContext db,
        IHubContext<RuntimeHub, IRuntimeClient> runtimeHub,
        ILogger<DispatchNextSessionHandler> logger)
    {
        _db = db;
        _runtimeHub = runtimeHub;
        _logger = logger;
    }

    public async Task Handle(AgentSessionTerminated notification, CancellationToken cancellationToken)
    {
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

        // chat-file-attachments — this queued session's attachments were
        // already stamped with its SessionId when it was first enqueued in
        // TurnDispatcher. Re-load them here so the daemon-bound prompt carries
        // the per-file path block; the stored Prompt stays the raw user text.
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
                "DispatchNextSessionHandler: concurrency conflict dispatching session {SessionId}.",
                next.Id);
            return;
        }

        var runtimeId = notification.RuntimeId;
        var dispatchedSessionId = next.Id;

        _ = Task.Run(async () =>
        {
            try
            {
                await _runtimeHub.Clients
                    .Group($"runtime-{runtimeId}")
                    .StartTurn(startPayload);

                _logger.LogInformation(
                    "DispatchNextSessionHandler: dispatched queued session {SessionId} to runtime {RuntimeId}.",
                    dispatchedSessionId, runtimeId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "DispatchNextSessionHandler: failed to send StartTurn for session {SessionId}.",
                    dispatchedSessionId);
            }
        });
    }
}

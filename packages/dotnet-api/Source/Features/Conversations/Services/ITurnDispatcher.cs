using MediatR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Source.Features.Attachments.Models;
using Source.Features.Attachments.Services;
using Source.Features.Conversations.Events;
using Source.Features.Conversations.Models;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;
using Source.Infrastructure;

namespace Source.Features.Conversations.Services;

public record DispatchTurnArgs(
    Guid ConversationId,
    Guid ProjectId,
    Guid BranchId,
    string Prompt,
    string? AgentId,
    string? EventOriginUserId,
    bool ForceQueue = false,
    Guid? ModelId = null,
    bool Yolo = false);

public record DispatchTurnResult(Guid SessionId, bool Queued, int? QueuePosition);

public interface ITurnDispatcher
{
    Task<DispatchTurnResult> DispatchTurnAsync(DispatchTurnArgs args, CancellationToken ct = default);
}

public sealed class TurnDispatcher : ITurnDispatcher
{
    private readonly ApplicationDbContext _db;
    private readonly IHubContext<RuntimeHub, IRuntimeClient> _runtimeHub;
    private readonly ILogger<TurnDispatcher> _logger;

    public TurnDispatcher(
        ApplicationDbContext db,
        IHubContext<RuntimeHub, IRuntimeClient> runtimeHub,
        ILogger<TurnDispatcher> logger)
    {
        _db = db;
        _runtimeHub = runtimeHub;
        _logger = logger;
    }

    public async Task<DispatchTurnResult> DispatchTurnAsync(DispatchTurnArgs args, CancellationToken ct = default)
    {
        var runtime = await _db.ProjectRuntimes
            .FirstOrDefaultAsync(r => r.BranchId == args.BranchId, ct);
        if (runtime is null)
        {
            throw new InvalidOperationException(
                $"TurnDispatcher: no runtime for branch {args.BranchId} (project {args.ProjectId}); cannot dispatch.");
        }

        if (runtime.ProjectId != args.ProjectId)
        {
            throw new InvalidOperationException(
                $"TurnDispatcher: branch {args.BranchId} resolves to a runtime in project {runtime.ProjectId}, but caller is dispatching for project {args.ProjectId}.");
        }

        var nowUtc = DateTime.UtcNow;

        var projectDefaults = await _db.Projects
            .AsNoTracking()
            .Where(p => p.Id == args.ProjectId)
            .Select(p => new
            {
                p.ModelId,
                ModelSlug = p.Model != null && p.Model.IsActive ? p.Model.Slug : null,
            })
            .FirstOrDefaultAsync(ct);
        if (projectDefaults is null)
        {
            throw new InvalidOperationException(
                $"TurnDispatcher: project {args.ProjectId} not found; cannot dispatch.");
        }

        string? resolvedModelSlug = null;
        Guid? resolvedModelId = null;
        if (args.ModelId is { } sessionModelId)
        {
            resolvedModelSlug = await _db.CursorModels
                .AsNoTracking()
                .Where(m => m.Id == sessionModelId && m.IsActive)
                .Select(m => m.Slug)
                .FirstOrDefaultAsync(ct);
            if (resolvedModelSlug is not null)
            {
                resolvedModelId = sessionModelId;
            }
        }

        if (resolvedModelSlug is null)
        {
            resolvedModelSlug = projectDefaults.ModelSlug;
            resolvedModelId = projectDefaults.ModelId;
        }

        var pullBeforeStart = !await _db.AgentSessions
            .AnyAsync(s => s.RuntimeId == runtime.Id, ct);

        var session = new AgentSession
        {
            ConversationId = args.ConversationId,
            RuntimeId = runtime.Id,
            Prompt = args.Prompt,
            Status = AgentSessionStatus.Pending,
            AgentId = args.AgentId,
            ModelId = resolvedModelId,
        };
        _db.AgentSessions.Add(session);

        // Seed the PromptReceived row at sequence 0. Cursor-native schema:
        // Kind = PromptReceived, prompt body lives in the first-class Text
        // column (no more opaque EventData JSON blob).
        var promptEvent = new AgentEvent
        {
            SessionId = session.Id,
            Sequence = 0,
            Kind = AgentEventKind.PromptReceived,
            Text = args.Prompt,
            CreatedAt = nowUtc,
        };
        _db.AgentEvents.Add(promptEvent);

        var conversation = await _db.Conversations
            .FirstOrDefaultAsync(c => c.Id == args.ConversationId, ct);
        if (conversation is null)
        {
            throw new InvalidOperationException(
                $"TurnDispatcher: conversation {args.ConversationId} not found; cannot dispatch.");
        }
        conversation.LastActivityAt = nowUtc;
        conversation.EventCount += 1;

        // chat-file-attachments — pull this conversation's draft attachments:
        // rows the browser has finished uploading (UploadedAt != null) but that
        // have not yet been associated with a turn (SessionId == null). Stamp
        // each with the freshly-created session id so past-message chip
        // rendering can later resolve "attachments for session X" without a
        // junction table, and so a re-send of the composer doesn't re-attach
        // the same files to a second turn. Tracked entities — the SessionId
        // write below is persisted by the single SaveChangesAsync at the end.
        var attachments = await _db.Attachments
            .Where(a => a.ConversationId == args.ConversationId
                && a.UploadedAt != null
                && a.SessionId == null)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct);

        foreach (var att in attachments)
        {
            att.SessionId = session.Id;
        }

        // Build the typed DTO snapshot for the broadcast — same Cursor-native
        // shape the daemon will continue to emit for subsequent events.
        var promptDto = new PromptReceivedEventDto(
            SessionId: session.Id,
            Sequence: 0,
            CreatedAt: nowUtc,
            Text: args.Prompt);

        session.RecordEventEmitted(new AgentEventEmitted(
            ConversationId: args.ConversationId,
            ProjectId: args.ProjectId,
            BranchId: args.BranchId,
            Kind: AgentEventKind.PromptReceived,
            Event: promptDto,
            OccurredAt: nowUtc));

        var hasActive = !args.ForceQueue && await _db.AgentSessions
            .AnyAsync(s => s.RuntimeId == runtime.Id
                && (s.Status == AgentSessionStatus.Running
                    || s.Status == AgentSessionStatus.Canceling), ct);

        bool queued;
        int? queuePosition;
        if (args.ForceQueue || hasActive)
        {
            var maxPos = await _db.AgentSessions
                .Where(s => s.RuntimeId == runtime.Id
                    && s.Status == AgentSessionStatus.Pending
                    && s.QueuePosition != null)
                .Select(s => (int?)s.QueuePosition)
                .MaxAsync(ct);
            queuePosition = (maxPos ?? 0) + 1;
            session.Enqueue(queuePosition.Value);
            queued = true;
        }
        else
        {
            session.Dispatch();
            queued = false;
            queuePosition = null;
        }

        await _db.SaveChangesAsync(ct);

        if (!queued)
        {
            // chat-file-attachments — augment the prompt the agent sees with
            // the per-file path block. The stored AgentSession.Prompt /
            // PromptReceived event keep the raw user text (above) so the chat
            // panel doesn't show the agent-facing prefix; only the daemon-bound
            // payload carries the augmented string. Empty attachments list →
            // byte-for-byte passthrough, identical to pre-attachments behaviour.
            var promptForAgent = PromptPrefixBuilder.BuildPromptWithAttachments(
                args.Prompt, attachments);

            var startPayload = new StartTurnPayload(
                SessionId: session.Id,
                ConversationId: args.ConversationId,
                Prompt: promptForAgent,
                Model: resolvedModelSlug,
                AgentId: args.AgentId,
                Yolo: args.Yolo,
                PullBeforeStart: pullBeforeStart);

            await _runtimeHub.Clients
                .Group($"runtime-{runtime.Id}")
                .StartTurn(startPayload);

            _logger.LogInformation(
                "TurnDispatcher: dispatched StartTurn for session {SessionId} (conversation {ConversationId}, runtime {RuntimeId}).",
                session.Id, args.ConversationId, runtime.Id);
        }
        else
        {
            _logger.LogInformation(
                "TurnDispatcher: queued session {SessionId} at position {QueuePosition} on runtime {RuntimeId}.",
                session.Id, queuePosition, runtime.Id);
        }

        return new DispatchTurnResult(session.Id, queued, queuePosition);
    }
}

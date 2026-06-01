using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Source.Features.Conversations.Models;
using Source.Infrastructure;
using Source.Infrastructure.Extensions;

namespace Source.Features.Conversations.Controllers;

/// <summary>
/// Read-only HTTP surface over the conversations / sessions / events tree —
/// the data the chat panel and admin views consume. Four endpoints, one shape
/// each: list conversations under a project, get a single conversation with
/// its sessions, get a single session, and page through a session's events.
///
/// <para><b>Why no MediatR.</b> Same pragmatic reasoning as
/// <see cref="Source.Features.RuntimeBootstrap.Controllers.BootstrapRunsController"/>
/// and <see cref="Source.Features.RuntimeLifecycle.Controllers.RuntimeStatusController"/>:
/// every endpoint is a thin passthrough over one or two reads with a projection.
/// Wrapping each in a query/handler pair would add eight files without changing
/// behaviour. The slice stays thin and the controller talks straight to the
/// DbContext.</para>
///
/// <para><b>Authorisation.</b> Default JWT bearer plus per-project ownership
/// gating. The list endpoint resolves ownership by the route's <c>projectId</c>
/// via <see cref="OwnershipExtensions.CallerOwnsProjectAsync"/>; per-conversation
/// endpoints (rename / archive / unarchive / get) use
/// <see cref="OwnershipExtensions.ResolveOwnedConversationAsync"/>; per-session
/// endpoints (get session / get events) use
/// <see cref="OwnershipExtensions.ResolveOwnedSessionAsync"/>. Both "no such
/// resource" and "exists but not yours" surface as <c>404</c> so cross-tenant
/// existence isn't leaked — same convention as
/// <see cref="Source.Features.Diffs.Controllers.DiffsController"/>.</para>
///
/// <para><b>Archived semantics.</b>
/// <see cref="ListConversations"/> respects the global query filter by default —
/// archived rows are hidden. Pass <c>includeArchived=true</c> to bypass the
/// filter and see everything. <see cref="GetConversation"/> always bypasses the
/// filter so an archived conversation can be retrieved directly by id (the chat
/// panel "unarchive" affordance and audit trails both need this).</para>
/// </summary>
[ApiController]
[Authorize]
[Tags("Conversations")]
public class ConversationsController : ControllerBase
{
    private readonly ApplicationDbContext _db;

    public ConversationsController(ApplicationDbContext db)
    {
        _db = db;
    }

    // ----------------------------------------------------------------------
    // List conversations under a project
    // ----------------------------------------------------------------------

    /// <summary>
    /// Page through the conversations belonging to a project, ordered by
    /// <see cref="Conversation.LastActivityAt"/> descending so the most-recently
    /// active conversation is always first. Backed by the
    /// <c>IX_Conversations_ProjectId_LastActivityAt_DESC</c> composite index from
    /// the conversations migration.
    ///
    /// <para><paramref name="includeArchived"/> bypasses the global query filter
    /// when true — the default UX hides archived rows but the archive screen
    /// needs them visible. <paramref name="take"/> is hard-capped at 200; bigger
    /// pages just waste memory and risk timing out the request.</para>
    /// </summary>
    [HttpGet("api/projects/{projectId:guid}/conversations")]
    [ProducesResponseType(typeof(List<ConversationSummary>), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<List<ConversationSummary>>> ListConversations(
        Guid projectId,
        [FromQuery] bool includeArchived = false,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        // Project-ownership gate — 404 (not 403) on mismatch so cross-tenant
        // project-id existence isn't leaked.
        if (!await _db.CallerOwnsProjectAsync(User, projectId, ct))
        {
            return NotFound();
        }

        // Defensive defaults — negative skip / non-positive take are pure user
        // error; coerce rather than 400. 200 is the cap (matches the rest of the
        // codebase).
        if (skip < 0) skip = 0;
        if (take < 1) take = 50;
        take = Math.Min(take, 200);

        // The Conversation entity has a global query filter on Status != Archived.
        // When includeArchived is true we have to call IgnoreQueryFilters() AND
        // re-apply the project filter ourselves — IgnoreQueryFilters drops every
        // filter on the entity, including any future tenant scoping.
        var query = includeArchived
            ? _db.Conversations.IgnoreQueryFilters().Where(c => c.ProjectId == projectId)
            : _db.Conversations.Where(c => c.ProjectId == projectId);

        var items = await query
            .OrderByDescending(c => c.LastActivityAt)
            .Skip(skip)
            .Take(take)
            .Select(c => new ConversationSummary(
                c.Id,
                c.ProjectId,
                c.BranchId,
                c.Title,
                c.Status,
                c.LastActivityAt,
                c.EventCount,
                // Latest session by CreatedAt — null cast keeps the projection
                // SQL-translatable when there are zero sessions (FirstOrDefault
                // on a value type would otherwise yield default(AgentSessionStatus),
                // which is Pending — wrong).
                c.Sessions
                    .OrderByDescending(s => s.CreatedAt)
                    .Select(s => (AgentSessionStatus?)s.Status)
                    .FirstOrDefault(),
                c.CreatedAt))
            .ToListAsync(ct);

        return Ok(items);
    }

    // ----------------------------------------------------------------------
    // Get conversation by id
    // ----------------------------------------------------------------------

    /// <summary>
    /// Fetch a single conversation plus its full session list (oldest first —
    /// the chat panel renders top-down). Always uses
    /// <c>IgnoreQueryFilters()</c> so an archived conversation is still
    /// retrievable by id; admin / audit / unarchive flows all depend on this.
    /// 404 when the id doesn't match an existing row.
    /// </summary>
    [HttpGet("api/conversations/{id:guid}")]
    [ProducesResponseType(typeof(ConversationDetail), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<ConversationDetail>> GetConversation(
        Guid id,
        CancellationToken ct)
    {
        // Ownership gate — non-owners (and probes for non-existent conversations)
        // get 404, never 403, so cross-tenant existence isn't leaked. The helper
        // only returns the entity when both the conversation exists and its
        // project's OwnerUserId matches the caller.
        if (await _db.ResolveOwnedConversationAsync(User, id, ct) is null)
        {
            return NotFound();
        }

        // IgnoreQueryFilters so archived conversations are reachable by id.
        // Project sessions inline; Sessions are ordered ASC by CreatedAt so the
        // chat panel can render them in the order they happened.
        //
        // chat-file-attachments: attachments are joined via the explicit
        // _db.Attachments subquery (not s.Attachments) because AgentSession
        // doesn't carry a navigation collection back to Attachment — the
        // relationship is configured as one-way on Attachment.Session with
        // WithMany(). The soft-delete query filter on Attachment is respected
        // automatically by this DbSet read.
        var detail = await _db.Conversations
            .IgnoreQueryFilters()
            .Where(c => c.Id == id)
            .Select(c => new ConversationDetail(
                c.Id,
                c.ProjectId,
                c.BranchId,
                c.Title,
                c.Status,
                c.LastActivityAt,
                c.EventCount,
                c.CreatedAt,
                c.Sessions
                    .OrderBy(s => s.CreatedAt)
                    .Select(s => new SessionSummary(
                        s.Id,
                        s.Status,
                        s.Prompt,
                        s.StartedAt,
                        s.CompletedAt,
                        s.FailureReason,
                        s.CreatedAt,
                        s.TotalCostUsd,
                        s.InputTokens,
                        s.OutputTokens,
                        s.CacheReadTokens,
                        s.CacheWriteTokens,
                        s.ReasoningTokens,
                        _db.Attachments
                            .Where(a => a.SessionId == s.Id)
                            .OrderBy(a => a.CreatedAt)
                            .Select(a => new AttachmentSummary(
                                a.Id,
                                a.FileName,
                                a.SizeBytes,
                                a.ContentType))
                            .ToList()))
                    .ToList()))
            .FirstOrDefaultAsync(ct);

        if (detail is null)
        {
            return NotFound();
        }

        return Ok(detail);
    }

    // ----------------------------------------------------------------------
    // Get session by id
    // ----------------------------------------------------------------------

    /// <summary>
    /// Fetch a single agent session plus the count of events recorded for it.
    /// 404 when the session id doesn't match an existing row. Uses a separate
    /// COUNT against <see cref="AgentEvent"/> rather than projecting
    /// <c>s.Events.Count</c> so the round trip stays cheap even when the session
    /// has thousands of events (the index on <c>AgentEvent.SessionId</c> via
    /// the composite PK covers it).
    /// </summary>
    [HttpGet("api/sessions/{id:guid}")]
    [ProducesResponseType(typeof(SessionDetail), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<SessionDetail>> GetSession(
        Guid id,
        CancellationToken ct)
    {
        // Ownership gate — null on either "no such session" or "not yours",
        // both surfaced uniformly as 404 so cross-tenant session-id existence
        // isn't leaked.
        var session = await _db.ResolveOwnedSessionAsync(User, id, ct);
        if (session is null)
        {
            return NotFound();
        }

        var eventCount = await _db.AgentEvents
            .CountAsync(e => e.SessionId == id, ct);

        return Ok(new SessionDetail(
            session.Id,
            session.ConversationId,
            session.Status,
            session.Prompt,
            session.AgentId,
            session.StartedAt,
            session.CompletedAt,
            session.FailureReason,
            session.CreatedAt,
            eventCount,
            session.TotalCostUsd,
            session.InputTokens,
            session.OutputTokens,
            session.CacheReadTokens,
            session.CacheWriteTokens,
            session.ReasoningTokens));
    }

    // ----------------------------------------------------------------------
    // Rename conversation
    // ----------------------------------------------------------------------

    /// <summary>
    /// Rename an existing conversation. The user can re-title a conversation
    /// from the chat panel — no business logic beyond a length / non-empty
    /// guard. <c>IgnoreQueryFilters()</c> so archived conversations are also
    /// renameable (useful when fixing a typo on an old, archived thread).
    ///
    /// <para>Returns the updated <see cref="ConversationDetail"/> so the client
    /// can refresh its view in one round-trip.</para>
    /// </summary>
    [HttpPost("api/conversations/{id:guid}/rename")]
    [ProducesResponseType(typeof(ConversationDetail), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<ConversationDetail>> RenameConversation(
        Guid id,
        [FromBody] RenameConversationRequest body,
        CancellationToken ct)
    {
        if (body is null)
        {
            return BadRequest(new { error = "Title cannot be empty" });
        }

        // Ownership gate — null on either "no such conversation" or "not yours",
        // both surfaced uniformly as 404. We re-load the conversation tracked
        // below for the actual mutation; the helper uses AsNoTracking and only
        // exists to gate the request.
        if (await _db.ResolveOwnedConversationAsync(User, id, ct) is null)
        {
            return NotFound();
        }

        // IgnoreQueryFilters so users can rename archived conversations too —
        // matches GetConversation's stance.
        var conversation = await _db.Conversations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (conversation is null)
        {
            return NotFound();
        }

        // Rich-entity method does the trim + non-empty + ≤200 invariant, flips
        // IsAutoTitled to false, and raises ConversationRenamed for SignalR
        // fan-out. The DomainEventInterceptor picks the event up on SaveChanges.
        var renameResult = conversation.Rename(body.Title);
        if (renameResult.IsFailure)
        {
            return BadRequest(new { error = renameResult.Error });
        }

        await _db.SaveChangesAsync(ct);

        // Re-project to the detail shape the client expects. Reuse the same
        // shape as GetConversation to keep the contract consistent — including
        // the chat-file-attachments subquery so a rename round-trip returns the
        // same per-session attachment metadata.
        var detail = await _db.Conversations
            .IgnoreQueryFilters()
            .Where(c => c.Id == id)
            .Select(c => new ConversationDetail(
                c.Id,
                c.ProjectId,
                c.BranchId,
                c.Title,
                c.Status,
                c.LastActivityAt,
                c.EventCount,
                c.CreatedAt,
                c.Sessions
                    .OrderBy(s => s.CreatedAt)
                    .Select(s => new SessionSummary(
                        s.Id,
                        s.Status,
                        s.Prompt,
                        s.StartedAt,
                        s.CompletedAt,
                        s.FailureReason,
                        s.CreatedAt,
                        s.TotalCostUsd,
                        s.InputTokens,
                        s.OutputTokens,
                        s.CacheReadTokens,
                        s.CacheWriteTokens,
                        s.ReasoningTokens,
                        _db.Attachments
                            .Where(a => a.SessionId == s.Id)
                            .OrderBy(a => a.CreatedAt)
                            .Select(a => new AttachmentSummary(
                                a.Id,
                                a.FileName,
                                a.SizeBytes,
                                a.ContentType))
                            .ToList()))
                    .ToList()))
            .FirstAsync(ct);

        return Ok(detail);
    }

    // ----------------------------------------------------------------------
    // Archive / unarchive conversation
    // ----------------------------------------------------------------------

    /// <summary>
    /// Move a conversation into <see cref="ConversationStatus.Archived"/>. The
    /// global query filter then hides it from the default conversation list;
    /// it remains reachable by id via <see cref="GetConversation"/> (which
    /// uses <c>IgnoreQueryFilters()</c>).
    ///
    /// <para>Idempotent in HTTP terms: archiving an already-archived
    /// conversation is a 204 no-op rather than a 4xx — clients can retry safely.</para>
    /// </summary>
    [HttpPost("api/conversations/{id:guid}/archive")]
    [ProducesResponseType(204)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult> ArchiveConversation(Guid id, CancellationToken ct)
    {
        // Ownership gate — null on either "no such conversation" or "not yours",
        // both surfaced uniformly as 404.
        if (await _db.ResolveOwnedConversationAsync(User, id, ct) is null)
        {
            return NotFound();
        }

        var conversation = await _db.Conversations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (conversation is null)
        {
            return NotFound();
        }

        if (conversation.Status == ConversationStatus.Archived)
        {
            // Already archived — nothing to do.
            return NoContent();
        }

        conversation.Status = ConversationStatus.Archived;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Reverse of <see cref="ArchiveConversation"/>: move a conversation back
    /// into <see cref="ConversationStatus.Active"/>. Idempotent — unarchiving
    /// an already-active conversation is a 204 no-op.
    /// </summary>
    [HttpPost("api/conversations/{id:guid}/unarchive")]
    [ProducesResponseType(204)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult> UnarchiveConversation(Guid id, CancellationToken ct)
    {
        // Ownership gate — null on either "no such conversation" or "not yours",
        // both surfaced uniformly as 404.
        if (await _db.ResolveOwnedConversationAsync(User, id, ct) is null)
        {
            return NotFound();
        }

        var conversation = await _db.Conversations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (conversation is null)
        {
            return NotFound();
        }

        if (conversation.Status == ConversationStatus.Active)
        {
            return NoContent();
        }

        conversation.Status = ConversationStatus.Active;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ----------------------------------------------------------------------
    // Page events of a session
    // ----------------------------------------------------------------------

    /// <summary>
    /// Page through the events of a session in <see cref="AgentEvent.Sequence"/>
    /// ascending order. Three cursor modes are supported:
    ///
    /// <list type="bullet">
    /// <item><paramref name="before"/> — an exclusive <i>tail</i> cursor (takes
    /// precedence if both are passed). Returns up to <paramref name="limit"/>
    /// events with sequence &lt; <c>before</c>, i.e. the page immediately BEFORE
    /// the cursor. The chat UI uses this to load newest-first and page older
    /// events on scroll-up; a caller passing a very large value (e.g.
    /// <see cref="long.MaxValue"/>) gets the latest/tail page. The response is
    /// still ascending by Sequence.</item>
    /// <item><paramref name="since"/> — an exclusive <i>forward</i> cursor: pass
    /// the highest sequence the client already has and this endpoint returns the
    /// next ascending batch.</item>
    /// <item>neither — ascending from the start.</item>
    /// </list>
    ///
    /// <paramref name="limit"/> is hard-capped at 1000 (events are tiny but the
    /// chat panel paginates in 200-row chunks; 1000 is the upper safety bound
    /// for replay-style backfills).
    ///
    /// <para>Returns 404 when the session itself doesn't exist so the client
    /// distinguishes "wrong id" from "no events yet". An existing session with
    /// no matching events returns 200 + an empty array.</para>
    /// </summary>
    [HttpGet("api/sessions/{id:guid}/events")]
    [ProducesResponseType(typeof(List<AgentEventDto>), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<List<AgentEventDto>>> GetEvents(
        Guid id,
        [FromQuery] long? since = null,
        [FromQuery] long? before = null,
        [FromQuery] int limit = 200,
        CancellationToken ct = default)
    {
        if (limit < 1) limit = 200;
        limit = Math.Min(limit, 1000);

        // Ownership gate doubles as the existence check — null on either "no
        // such session" or "not yours", both surfaced as 404 so cross-tenant
        // session-id existence isn't leaked. The client uses 404 vs an empty
        // 200 array to distinguish "wrong id" from "idle session".
        if (await _db.ResolveOwnedSessionAsync(User, id, ct) is null)
        {
            return NotFound();
        }

        // Pull the rows first, then project per-kind on the C# side — the
        // discriminated union shape isn't trivially translatable to SQL
        // (EF Core can't fan a single Select into the right concrete subtype
        // per row). The N+1 nullable-column reads on AgentEvent are cheap;
        // the chat panel pages 200 rows at a time.
        var baseQuery = _db.AgentEvents.Where(e => e.SessionId == id);

        List<AgentEvent> rows;
        if (before.HasValue)
        {
            // Tail/backward page: grab the highest-sequenced events below the
            // cursor by ordering DESC + Take, then flip the materialized list
            // back to ascending so the projected output stays ascending.
            rows = await baseQuery
                .Where(e => e.Sequence < before.Value)
                .OrderByDescending(e => e.Sequence)
                .Take(limit)
                .ToListAsync(ct);
            rows.Reverse();
        }
        else
        {
            var query = baseQuery;
            if (since.HasValue)
            {
                query = query.Where(e => e.Sequence > since.Value);
            }

            rows = await query
                .OrderBy(e => e.Sequence)
                .Take(limit)
                .ToListAsync(ct);
        }

        var events = rows.Select(ProjectAgentEvent).ToList();

        return Ok(events);
    }

    // ----------------------------------------------------------------------
    // Get the run-result aggregate for a session
    // ----------------------------------------------------------------------

    /// <summary>
    /// Fetch the per-turn aggregate result for a completed session. Drives the
    /// chat panel's turn footer (<i>"Finished in 14.2s · claude-sonnet-4 · 5
    /// files edited · view PR ↗"</i>). Returns 404 if the session doesn't
    /// exist or hasn't completed yet — clients distinguish "no footer to show"
    /// from "session running" via the same 404 status, so callers should
    /// already know the session is terminal before hitting this endpoint
    /// (the events list's terminal <see cref="StatusEventDto"/> tells them
    /// when).
    /// </summary>
    [HttpGet("api/sessions/{id:guid}/run-result")]
    [ProducesResponseType(typeof(RunResultDto), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<RunResultDto>> GetRunResult(
        Guid id,
        CancellationToken ct = default)
    {
        if (await _db.ResolveOwnedSessionAsync(User, id, ct) is null)
        {
            return NotFound();
        }

        var row = await _db.RunResults
            .Where(r => r.SessionId == id)
            .Select(r => new
            {
                r.SessionId,
                r.DurationMs,
                r.Model,
                r.GitBranch,
                r.GitPrUrl,
                r.ArtifactsJson,
                r.CreatedAt,
            })
            .FirstOrDefaultAsync(ct);

        if (row is null)
        {
            return NotFound();
        }

        var artifacts = DeserializeArtifacts(row.ArtifactsJson);
        return Ok(new RunResultDto(
            SessionId: row.SessionId,
            DurationMs: row.DurationMs,
            Model: row.Model,
            GitBranch: row.GitBranch,
            GitPrUrl: row.GitPrUrl,
            Artifacts: artifacts,
            CreatedAt: row.CreatedAt));
    }

    private static AgentEventDto ProjectAgentEvent(AgentEvent e)
    {
        return e.Kind switch
        {
            AgentEventKind.PromptReceived => new PromptReceivedEventDto(
                e.SessionId, e.Sequence, e.CreatedAt, e.Text ?? string.Empty),
            AgentEventKind.AssistantText => new AssistantTextEventDto(
                e.SessionId, e.Sequence, e.CreatedAt, e.Text ?? string.Empty),
            AgentEventKind.Thinking => new ThinkingEventDto(
                e.SessionId, e.Sequence, e.CreatedAt, e.Text ?? string.Empty, e.ThinkingDurationMs),
            AgentEventKind.ToolUse => new ToolUseEventDto(
                e.SessionId,
                e.Sequence,
                e.CreatedAt,
                CallId: e.CallId ?? string.Empty,
                Name: e.ToolName ?? string.Empty,
                Status: e.ToolStatus ?? AgentEventToolStatus.Running,
                Args: e.Args,
                Result: e.Result,
                ArgsTruncated: e.ArgsTruncated ?? false,
                ResultTruncated: e.ResultTruncated ?? false),
            AgentEventKind.Status => new StatusEventDto(
                e.SessionId,
                e.Sequence,
                e.CreatedAt,
                Status: e.RunStatus ?? AgentEventRunStatus.Creating,
                Message: e.StatusMessage),
            AgentEventKind.Task => new TaskEventDto(
                e.SessionId,
                e.Sequence,
                e.CreatedAt,
                TaskId: e.TaskId,
                Title: e.TaskTitle),
            _ => throw new InvalidOperationException(
                $"Unknown AgentEventKind {e.Kind} on session {e.SessionId} seq {e.Sequence}"),
        };
    }

    private static List<ArtifactDto> DeserializeArtifacts(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]")
        {
            return new List<ArtifactDto>();
        }
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<ArtifactDto>>(
                json,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
                ?? new List<ArtifactDto>();
        }
        catch (System.Text.Json.JsonException)
        {
            return new List<ArtifactDto>();
        }
    }
}

/// <summary>
/// Body shape for <c>POST /api/conversations/{id}/rename</c>. A bare title
/// string is enough — the controller trims and validates length / non-empty.
/// </summary>
public record RenameConversationRequest(string Title);

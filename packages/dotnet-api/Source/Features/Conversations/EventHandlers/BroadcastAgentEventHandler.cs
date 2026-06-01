using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Source.Features.Conversations.Events;
using Source.Features.Conversations.Models;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;
using Source.Infrastructure;
using Source.Shared.Events;

namespace Source.Features.Conversations.EventHandlers;

/// <summary>
/// Bridges the domain layer to connected React clients for agent events: every
/// time <c>RuntimeHub.EmitEvent</c> raises an <see cref="AgentEventEmitted"/>
/// the dispatcher invokes this handler, which fans the fully-typed
/// <see cref="AgentEventNotification"/> out to the <c>branch-{id}</c> SignalR
/// group and (additively) the parent <c>workspace-{workspaceId}</c> group.
///
/// <para><b>Card 3 (cursor-native-chat-ux).</b> The notification now carries
/// the full <see cref="AgentEventDto"/> snapshot inline — the React client
/// drops the row straight into its conversation log without a REST refetch.
/// The hub already projected the row into the discriminated union as part of
/// the EmitEvent transaction, so this handler is pure fan-out.</para>
///
/// <para>When the underlying event corresponds to a terminal Status frame
/// that included a per-turn <see cref="RunResultDto"/>, the handler also
/// pushes a <see cref="RunResultNotification"/> on the same groups so the
/// chat panel's turn footer renders live.</para>
///
/// <para>Branch-scoped (not project-scoped) so live ticks from one branch's
/// conversations don't leak into other branches' chat tabs after CopyBranch
/// (a project owns N ProjectBranches, each with its own conversations). The
/// workspace-group fan-out lets the agent-native sidebar show live "running"
/// pulses across every project.</para>
///
/// <para>Exception-swallowing reliability contract: the persistence side of
/// the event was already committed inside the same SaveChanges that scheduled
/// this handler; a SignalR fault here must not poison the rest of the
/// dispatcher chain. Worst case clients miss a live tick and catch up via the
/// replay endpoint or by reloading the conversation.</para>
/// </summary>
public class BroadcastAgentEventHandler : IEventHandler<AgentEventEmitted>
{
    private readonly IHubContext<AgentHub, IAgentClient> _hub;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<BroadcastAgentEventHandler> _logger;

    public BroadcastAgentEventHandler(
        IHubContext<AgentHub, IAgentClient> hub,
        ApplicationDbContext db,
        ILogger<BroadcastAgentEventHandler> logger)
    {
        _hub = hub;
        _db = db;
        _logger = logger;
    }

    public async Task Handle(AgentEventEmitted notification, CancellationToken cancellationToken)
    {
        var payload = new AgentEventNotification(
            ConversationId: notification.ConversationId,
            ProjectId: notification.ProjectId,
            BranchId: notification.BranchId,
            Event: notification.Event);

        try
        {
            await _hub.Clients
                .Group($"branch-{notification.BranchId}")
                .AgentEvent(payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to broadcast AgentEventEmitted to branch group for session {SessionId} seq {Sequence} (branch {BranchId}, project {ProjectId}); persistence is unaffected.",
                notification.SessionId,
                notification.Sequence,
                notification.BranchId,
                notification.ProjectId);
        }

        // Additive workspace-group broadcast. Lets the agent-native sidebar
        // show live "running" pulses for turn lifecycle events across every
        // project in the workspace — not just the one in the active tab. The
        // payload is identical; the client filters / routes on the embedded
        // ConversationId (and infers ProjectId via its sidebar projection
        // state).
        Guid? workspaceId = null;
        try
        {
            workspaceId = await _db.Projects
                .AsNoTracking()
                .Where(p => p.Id == notification.ProjectId)
                .Select(p => (Guid?)p.WorkspaceId)
                .FirstOrDefaultAsync(cancellationToken);
            if (workspaceId is { } wsId)
            {
                await _hub.Clients
                    .Group($"workspace-{wsId}")
                    .AgentEvent(payload);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to broadcast AgentEventEmitted to workspace group for session {SessionId} seq {Sequence} (project {ProjectId}); persistence is unaffected.",
                notification.SessionId,
                notification.Sequence,
                notification.ProjectId);
        }

        // Per-turn run-result fan-out — only fires on terminal Status frames
        // that came with a RunResult aggregate. Re-read from DB (the typed
        // payload already lives in RunResults thanks to the hub's upsert) so
        // we ship a consistent snapshot rather than re-deserialising the
        // daemon's wire shape.
        if (notification.Kind == AgentEventKind.Status &&
            notification.Event is StatusEventDto status &&
            IsTerminal(status.Status))
        {
            try
            {
                var runResult = await _db.RunResults
                    .AsNoTracking()
                    .Where(r => r.SessionId == notification.SessionId)
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
                    .FirstOrDefaultAsync(cancellationToken);

                if (runResult is not null)
                {
                    var artifacts = DeserializeArtifacts(runResult.ArtifactsJson);
                    var dto = new RunResultDto(
                        SessionId: runResult.SessionId,
                        DurationMs: runResult.DurationMs,
                        Model: runResult.Model,
                        GitBranch: runResult.GitBranch,
                        GitPrUrl: runResult.GitPrUrl,
                        Artifacts: artifacts,
                        CreatedAt: runResult.CreatedAt);

                    var resultPayload = new RunResultNotification(
                        ConversationId: notification.ConversationId,
                        ProjectId: notification.ProjectId,
                        BranchId: notification.BranchId,
                        Result: dto);

                    await _hub.Clients
                        .Group($"branch-{notification.BranchId}")
                        .RunResult(resultPayload);

                    if (workspaceId is { } wsId2)
                    {
                        await _hub.Clients
                            .Group($"workspace-{wsId2}")
                            .RunResult(resultPayload);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to broadcast RunResult for session {SessionId} (branch {BranchId}, project {ProjectId}); chat footer will need a REST refetch.",
                    notification.SessionId,
                    notification.BranchId,
                    notification.ProjectId);
            }
        }
    }

    private static bool IsTerminal(AgentEventRunStatus status) => status switch
    {
        AgentEventRunStatus.Finished => true,
        AgentEventRunStatus.Error => true,
        AgentEventRunStatus.Cancelled => true,
        AgentEventRunStatus.Expired => true,
        _ => false,
    };

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

using Source.Features.Conversations.Events;
using Source.Infrastructure;
using Source.Shared.Events;

namespace Source.Features.Conversations.EventHandlers;

/// <summary>
/// Denormalizes per-turn cost + token usage onto the parent
/// <see cref="Source.Features.Conversations.Models.AgentSession"/>. Fired on
/// <see cref="AgentSessionTerminated"/>.
///
/// <para>TODO(card 3 / cost follow-up spec): the Cursor-native AgentEvent
/// schema no longer carries the Anthropic-shaped TurnCompleted/TurnFailed
/// JSON envelope this handler used to scrape <c>totalCostUsd</c> +
/// <c>usage</c> off. Cost / token capture is explicitly OUT OF SCOPE for
/// the cursor-native-chat-ux spec — see §1 "Cost and token display is out
/// of scope. Separate follow-up spec." The handler is kept as a no-op stub
/// so the <see cref="IEventHandler{T}"/> registration + DI graph still
/// resolve; the follow-up cost spec re-wires it (likely from
/// <see cref="Source.Features.Conversations.Models.RunResult"/> plus future
/// Cursor token-usage payloads).</para>
/// </summary>
public class RecordSessionCostHandler : IEventHandler<AgentSessionTerminated>
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<RecordSessionCostHandler> _logger;

    public RecordSessionCostHandler(
        ApplicationDbContext db,
        ILogger<RecordSessionCostHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public Task Handle(AgentSessionTerminated notification, CancellationToken cancellationToken)
    {
        // No-op stub — see class doc for the rationale.
        _ = _db;
        _logger.LogDebug(
            "RecordSessionCostHandler: cost capture is stubbed pending the cost follow-up spec (session {SessionId}, final status {FinalStatus}).",
            notification.SessionId, notification.FinalStatus);
        return Task.CompletedTask;
    }
}

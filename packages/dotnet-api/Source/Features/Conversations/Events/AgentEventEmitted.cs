using Source.Features.Conversations.Models;
using Source.Shared.Events;

namespace Source.Features.Conversations.Events;

/// <summary>
/// Raised whenever the daemon emits an event into an <see cref="AgentSession"/>
/// via <c>RuntimeHub.EmitEvent</c>. Carries the fully-projected
/// <see cref="AgentEventDto"/> snapshot so downstream broadcast handlers
/// can fan the typed payload out to web clients without a re-fetch.
///
/// <para>Implements <see cref="IEntityDomainEvent"/> so the
/// <c>DomainEventInterceptor</c> populates <c>EntityId</c> / <c>EntityType</c>
/// on <see cref="StoredDomainEvent"/> for indexed audit lookups (the entity is
/// the <see cref="AgentSession"/> the event was emitted into).</para>
///
/// <para>The convenience accessors (<see cref="SessionId"/>,
/// <see cref="Sequence"/>, <see cref="Kind"/>) read straight off
/// <see cref="Event"/> so handlers can route without dereferencing the union.</para>
/// </summary>
public record AgentEventEmitted(
    Guid ConversationId,
    Guid ProjectId,
    Guid BranchId,
    AgentEventKind Kind,
    AgentEventDto Event,
    DateTime OccurredAt
) : IEntityDomainEvent
{
    /// <summary>Convenience ctor that stamps OccurredAt with the current UTC clock.</summary>
    public AgentEventEmitted(
        Guid conversationId,
        Guid projectId,
        Guid branchId,
        AgentEventKind kind,
        AgentEventDto @event)
        : this(conversationId, projectId, branchId, kind, @event, DateTime.UtcNow) { }

    /// <summary>Session this event belongs to. Sourced off the embedded DTO.</summary>
    public Guid SessionId => Event.SessionId;

    /// <summary>Per-session monotonic sequence number. Sourced off the embedded DTO.</summary>
    public long Sequence => Event.Sequence;

    string IEntityDomainEvent.EntityId => SessionId.ToString();
    string IEntityDomainEvent.EntityType => nameof(AgentSession);
}

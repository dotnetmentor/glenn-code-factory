using Source.Features.Conversations.Models;
using Source.Shared.Events;

namespace Source.Features.Conversations.Events;

/// <summary>
/// Raised when a user reorders the queued (Pending + non-null
/// <see cref="AgentSession.QueuePosition"/>) sessions on a runtime via the
/// reorder endpoint. The event is <em>runtime-scoped</em> rather than
/// session-scoped: a single drag-drop typically renumbers several sessions
/// in one shot and we want a single audit row capturing the whole reshuffle,
/// not N near-identical rows.
///
/// <para><see cref="NewOrder"/> is the post-handler order — index 0 is the
/// session that will dispatch first when the runtime frees up. The list
/// matches the request payload, but it's also the contract the audit log
/// reader relies on, so we surface it explicitly on the event.</para>
///
/// <para>Only <see cref="IDomainEvent"/> (not <see cref="IEntityDomainEvent"/>)
/// because there's no single owning entity row — the event is raised against
/// the runtime, not any one session. The
/// <c>DomainEventInterceptor</c> stores it without populating
/// <c>EntityId</c>/<c>EntityType</c>, which is fine: <c>RuntimeId</c> is on
/// the JSONB payload and queryable directly.</para>
/// </summary>
public record QueueReordered(
    Guid RuntimeId,
    IReadOnlyList<Guid> NewOrder,
    string ActorUserId
) : IDomainEvent
{
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
}

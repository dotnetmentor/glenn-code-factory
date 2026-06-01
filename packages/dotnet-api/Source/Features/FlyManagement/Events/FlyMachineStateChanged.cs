using Source.Shared.Events;

namespace Source.Features.FlyManagement.Events;

/// <summary>
/// Published whenever Fly delivers a verified machine-state-change webhook. Subscribers
/// (e.g. the runtime-lifecycle feature, once it lands) react to the new state to keep our
/// internal projection of every Fly machine's runtime state in sync.
///
/// <para>The event is raised after the audit row is written and the HMAC has been
/// validated, so handlers can assume the payload is authentic. <see cref="FlyEventId"/>
/// is the dedup key Fly assigns each delivery — handlers that need their own idempotency
/// guarantee should key off that field.</para>
///
/// <para>Implements the bare <see cref="IDomainEvent"/> rather than
/// <c>IEntityDomainEvent</c> because the entity these events relate to (a Fly machine)
/// lives outside our DB — it has no application-side <c>EntityId</c>/<c>EntityType</c>
/// to project onto the <c>StoredDomainEvents</c> indexes.</para>
/// </summary>
public record FlyMachineStateChanged(
    string MachineId,
    string NewState,
    string? PreviousState,
    DateTime OccurredAt,
    string FlyEventId) : IDomainEvent;

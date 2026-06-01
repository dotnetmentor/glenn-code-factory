using Source.Shared.Events;

namespace Source.Features.ProjectSecrets.Events;

/// <summary>
/// Domain event raised when a project's secret bundle has changed — a key was
/// added, an existing key's value was rotated, or a key was deleted.
///
/// <para><b>Why a plain <see cref="IDomainEvent"/>, not <see cref="IEntityDomainEvent"/>.</b>
/// Deletes raise this event from a soft-deleted (post-SaveChanges) row, which
/// makes the entity-scoped collection unreliable as the carrier — by the time
/// the dispatch happens the entity may no longer be tracked. We publish this
/// event manually from the command handlers via <c>IMediator.Publish</c> after
/// <c>SaveChangesAsync</c>, mirroring the runtime-scoped pattern used in this
/// codebase. The Card 4 handler will fan it out to the daemon group via SignalR
/// and tear down env files for revoked keys.</para>
///
/// <para><b>Granularity.</b> One event per command (single key changed). The
/// daemon's bootstrap handshake reconciles the full bundle on every push, so
/// the event only needs to carry enough context to invalidate any local
/// caching layer.</para>
/// </summary>
/// <param name="ProjectId">Project whose secret bundle changed.</param>
/// <param name="ChangedKey">Env-var name that was created, updated, or deleted.</param>
/// <param name="Deleted"><c>true</c> when the action was a delete; <c>false</c>
/// for create / update.</param>
/// <param name="BranchId">Branch scope of the changed row. <c>null</c> means the
/// change targeted a project-wide default (applies to every branch unless
/// overridden); a non-null value means a branch-specific row changed. Downstream
/// handlers use this to push the change only to runtimes for which the change is
/// branch-effective — a project-wide change must not clobber a branch override,
/// and a branch-specific change only reaches runtimes pinned to that branch.</param>
public record SecretsChanged(
    Guid ProjectId,
    string ChangedKey,
    bool Deleted,
    Guid? BranchId) : IDomainEvent
{
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
}

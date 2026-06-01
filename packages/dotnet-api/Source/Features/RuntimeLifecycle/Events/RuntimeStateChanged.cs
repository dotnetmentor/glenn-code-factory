using Source.Features.RuntimeLifecycle.Models;
using Source.Shared.Events;

namespace Source.Features.RuntimeLifecycle.Events;

/// <summary>
/// Raised whenever a <see cref="ProjectRuntime"/> moves from one
/// <see cref="RuntimeState"/> to another via <c>TransitionTo</c>. The
/// <see cref="PersistRuntimeStateEventHandler"/> reacts to this and writes
/// an append-only <see cref="RuntimeStateEvent"/> audit row.
///
/// <para><see cref="FromState"/> is nullable because the very first transition
/// for a runtime — the implicit Pending insert — has no prior state. After
/// that point every transition has a non-null <c>FromState</c>.</para>
/// </summary>
public record RuntimeStateChanged(
    Guid RuntimeId,
    Guid ProjectId,
    Guid BranchId,
    RuntimeState? FromState,
    RuntimeState ToState,
    string Reason,
    string TriggeredBy,
    string? Metadata,
    DateTime OccurredAt
) : IEntityDomainEvent
{
    public RuntimeStateChanged(
        Guid runtimeId,
        Guid projectId,
        Guid branchId,
        RuntimeState? fromState,
        RuntimeState toState,
        string reason,
        string triggeredBy,
        string? metadata = null)
        : this(runtimeId, projectId, branchId, fromState, toState, reason, triggeredBy, metadata, DateTime.UtcNow) { }

    string IEntityDomainEvent.EntityId => RuntimeId.ToString();
    string IEntityDomainEvent.EntityType => "ProjectRuntime";
}

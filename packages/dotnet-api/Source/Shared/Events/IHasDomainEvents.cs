namespace Source.Shared.Events;

/// <summary>
/// Contract for entities that can raise domain events.
/// Implemented by Entity base class (for normal entities) and directly
/// by User (which inherits from IdentityUser and can't use Entity base).
/// </summary>
public interface IHasDomainEvents
{
    IReadOnlyList<IDomainEvent> DomainEvents { get; }
    void ClearDomainEvents();
}

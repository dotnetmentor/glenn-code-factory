using System.ComponentModel.DataAnnotations.Schema;

namespace Source.Shared.Events;

/// <summary>
/// Base class for entities that raise domain events.
/// Use this for all entities EXCEPT those already inheriting from IdentityUser
/// (those should implement IHasDomainEvents directly).
/// </summary>
public abstract class Entity : IHasDomainEvents
{
    private readonly List<IDomainEvent> _domainEvents = new();

    [NotMapped]
    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void RaiseDomainEvent(IDomainEvent domainEvent)
    {
        _domainEvents.Add(domainEvent);
    }

    public void ClearDomainEvents()
    {
        _domainEvents.Clear();
    }
}

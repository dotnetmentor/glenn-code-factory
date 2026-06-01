using System.Security.Claims;
using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Source.Shared.Events;

namespace Source.Infrastructure.Interceptors;

/// <summary>
/// Two-phase interceptor:
/// 1. SavingChangesAsync — persists events to StoredDomainEvents (same transaction)
/// 2. SavedChangesAsync — dispatches events via MediatR (after commit)
/// </summary>
public class DomainEventInterceptor : SaveChangesInterceptor
{
    private readonly IPublisher _publisher;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private List<IDomainEvent> _pendingEvents = new();

    public DomainEventInterceptor(IPublisher publisher, IHttpContextAccessor httpContextAccessor)
    {
        _publisher = publisher;
        _httpContextAccessor = httpContextAccessor;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        if (eventData.Context is null)
            return await base.SavingChangesAsync(eventData, result, ct);

        // Collect events from all tracked entities that implement IHasDomainEvents.
        // Do NOT clear entity events here — if SaveChanges fails, events must
        // remain on the entities so a retry can still dispatch them.
        _pendingEvents = eventData.Context.ChangeTracker
            .Entries<IHasDomainEvents>()
            .SelectMany(e => e.Entity.DomainEvents)
            .ToList();

        if (_pendingEvents.Count == 0)
            return await base.SavingChangesAsync(eventData, result, ct);

        // Persist events in the same transaction
        var userId = _httpContextAccessor.HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier);

        foreach (var domainEvent in _pendingEvents)
        {
            var storedEvent = new StoredDomainEvent
            {
                Id = Guid.NewGuid(),
                EventType = domainEvent.GetType().Name,
                Payload = JsonSerializer.Serialize(domainEvent, domainEvent.GetType()),
                UserId = userId,
                OccurredAt = domainEvent.OccurredAt
            };

            if (domainEvent is IEntityDomainEvent entityEvent)
            {
                storedEvent.EntityId = entityEvent.EntityId;
                storedEvent.EntityType = entityEvent.EntityType;
            }

            eventData.Context.Set<StoredDomainEvent>().Add(storedEvent);
        }

        return await base.SavingChangesAsync(eventData, result, ct);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default)
    {
        // Dispatch events via MediatR after successful commit
        var events = _pendingEvents.ToList();
        _pendingEvents.Clear();

        if (events.Count > 0 && eventData.Context is not null)
        {
            foreach (var entry in eventData.Context.ChangeTracker.Entries<IHasDomainEvents>())
            {
                entry.Entity.ClearDomainEvents();
            }
        }

        foreach (var domainEvent in events)
            await _publisher.Publish(domainEvent, ct);

        return await base.SavedChangesAsync(eventData, result, ct);
    }
}

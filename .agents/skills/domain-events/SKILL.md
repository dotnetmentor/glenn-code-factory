---
name: domain-events
description: >
  Domain events, rich entity methods, and event store traceability for the .NET backend.
  Use when: (1) Adding business logic to an entity (state transitions, invariants),
  (2) Creating a new domain event, (3) Adding side effects that react to something that happened,
  (4) Making a new entity "event-aware" with IHasDomainEvents,
  (5) Querying the StoredDomainEvents audit trail,
  (6) Understanding when to use rich entity methods vs keeping logic in handlers.
---

# Domain Events & Rich Entities

## How It Works

1. Entity method mutates state + calls `RaiseDomainEvent()`
2. Handler calls entity method, then `SaveChangesAsync()`
3. `DomainEventInterceptor` (before commit) persists events to `StoredDomainEvents` table (same transaction)
4. `DomainEventInterceptor` (after commit) dispatches events via MediatR for side effects

**Never manually call `_mediator.Publish()` for domain events.** The interceptor handles it.

---

## When to Use Rich Entity Methods

| Scenario | Approach |
|----------|----------|
| State transition with invariants (cancel, approve, complete) | **Rich method on entity** |
| Simple field update, mostly CRUD | **Direct mutation in handler** |
| Logic that needs async DB lookups (uniqueness checks) | **Keep in handler**, call entity method for the state change part |
| Side effects (email, notifications, audit) | **Event handler** reacting to domain event |

---

## Key Files

| File | Purpose |
|------|---------|
| `Source/Shared/Events/IHasDomainEvents.cs` | Interface — what the interceptor scans for |
| `Source/Shared/Events/Entity.cs` | Base class for non-Identity entities |
| `Source/Shared/Events/IDomainEvent.cs` | Event marker interface (requires `OccurredAt`) |
| `Source/Shared/Events/IEntityDomainEvent.cs` | Optional — adds `EntityId`/`EntityType` for indexed querying |
| `Source/Shared/Events/StoredDomainEvent.cs` | Persisted audit record (JSONB payload) |
| `Source/Infrastructure/Interceptors/DomainEventInterceptor.cs` | Two-phase persist + dispatch |

---

## Adding a Rich Method to an Existing Entity

### Entities inheriting from `Entity` base class

```csharp
public class Booking : Entity
{
    public BookingStatus Status { get; private set; }

    public Result Cancel(string? reason)
    {
        if (Status != BookingStatus.Confirmed)
            return Result.Failure("Only confirmed bookings can be cancelled");

        Status = BookingStatus.Cancelled;
        UpdatedAt = DateTime.UtcNow;
        RaiseDomainEvent(new BookingCancelled(Id, ResourceId));
        return Result.Success();
    }
}
```

### User entity (inherits IdentityUser, implements IHasDomainEvents directly)

User can't use Entity base class. It implements `IHasDomainEvents` directly with the same 5-line plumbing. See `Source/Features/Users/Models/User.cs` for the pattern.

---

## Creating a New Domain Event

Place in the feature's `Events/` folder. Name in **past tense**.

Use `IEntityDomainEvent` when the event relates to a specific entity (enables indexed querying).
Use `IDomainEvent` for system-wide events not tied to one entity.

```csharp
using Source.Shared.Events;

namespace Source.Features.Bookings.Events;

public record BookingCancelled(
    Guid BookingId,
    Guid ResourceId,
    DateTime OccurredAt
) : IEntityDomainEvent
{
    public BookingCancelled(Guid bookingId, Guid resourceId)
        : this(bookingId, resourceId, DateTime.UtcNow) { }

    string IEntityDomainEvent.EntityId => BookingId.ToString();
    string IEntityDomainEvent.EntityType => "Booking";
}
```

Always provide a convenience constructor that auto-fills `OccurredAt`.

---

## Creating an Event Handler (Side Effects)

Place in the feature's `EventHandlers/` folder. One handler per side effect.

```csharp
using Source.Shared.Events;

namespace Source.Features.Bookings.EventHandlers;

public class NotifyOnBookingCancelled : IEventHandler<BookingCancelled>
{
    private readonly IEmailService _emailService;

    public NotifyOnBookingCancelled(IEmailService emailService)
    {
        _emailService = emailService;
    }

    public async Task Handle(BookingCancelled notification, CancellationToken ct)
    {
        await _emailService.SendEmailAsync(...);
    }
}
```

Adding a new side effect = add a new handler. Zero changes to existing code.

---

## Making a New Entity Event-Aware

### Standard entity (not IdentityUser)

Inherit from `Entity`:

```csharp
using Source.Shared.Events;

public class Booking : Entity
{
    public Guid Id { get; private set; }
    // ... properties

    private Booking() { } // EF Core

    public static Booking Create(Guid resourceId, ...)
    {
        var booking = new Booking { Id = Guid.NewGuid(), ... };
        booking.RaiseDomainEvent(new BookingCreated(booking.Id));
        return booking;
    }
}
```

### Entity inheriting from another base (like IdentityUser)

Implement `IHasDomainEvents` directly:

```csharp
using System.ComponentModel.DataAnnotations.Schema;
using Source.Shared.Events;

public class User : IdentityUser, IHasDomainEvents
{
    private readonly List<IDomainEvent> _domainEvents = new();

    [NotMapped]
    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();
    protected void RaiseDomainEvent(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);
    public void ClearDomainEvents() => _domainEvents.Clear();

    // ... rest of entity
}
```

---

## Handler Pattern with Rich Entity

```csharp
public async Task<Result<Response>> Handle(CancelBookingCommand request, CancellationToken ct)
{
    // 1. Load entity
    var booking = await _context.Bookings.FindAsync(request.BookingId);
    if (booking is null) return Result.Failure<Response>("Not found");

    // 2. Call entity method (validates + mutates + raises event)
    var result = booking.Cancel(request.Reason);
    if (result.IsFailure) return Result.Failure<Response>(result.Error!);

    // 3. Save — interceptor persists events + dispatches them
    await _context.SaveChangesAsync(ct);

    return Result.Success(new Response { ... });
}
```

For UserManager-based entities, `_userManager.UpdateAsync(user)` calls SaveChanges internally — the interceptor still fires.

---

## StoredDomainEvents Table

All events are automatically persisted with:
- `EventType` — event class name (e.g. "BookingCancelled")
- `Payload` — full JSON serialization (JSONB column)
- `EntityId` / `EntityType` — auto-populated when event implements `IEntityDomainEvent`
- `UserId` — auto-captured from HttpContext
- `OccurredAt` — from the event's OccurredAt property

Indexed on: `EventType`, `OccurredAt`, `(EntityType, EntityId)`.

---

## Automatic Audit Fields

`ApplicationDbContext.SaveChangesAsync` auto-sets timestamps. **Never set these manually.**

| Interface | Fields | When |
|-----------|--------|------|
| `IAuditable` | `CreatedAt`, `UpdatedAt` | Added → both set. Modified → UpdatedAt set. |
| `ISoftDelete` | `DeletedAt`, `DeletedBy` | When `IsDeleted` flipped to true. `DeletedBy` = current user from HttpContext. |

To make a new entity auditable:
```csharp
public class Booking : Entity, IAuditable, ISoftDelete
{
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
}
```

Soft delete in handler — just flip the flag:
```csharp
booking.IsDeleted = true;
await _context.SaveChangesAsync(ct); // DeletedAt, DeletedBy, UpdatedAt all set automatically
```

---

## What NOT to Do

- **Don't call `_mediator.Publish()`** in handlers for domain events — the interceptor does this
- **Don't inject services into entities** — entities are pure, no DbContext, no external calls
- **Don't make every entity rich** — CRUD-only entities stay anemic (no Entity base needed)
- **Don't put async logic in entity methods** — async DB lookups stay in the handler
- **Don't set `CreatedAt`/`UpdatedAt`/`DeletedAt`/`DeletedBy` manually** — the DbContext handles it

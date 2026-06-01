using Source.Shared.Events;

namespace Source.Features.Users.Events;

public record UserCreated(
    string UserId,
    string Email,
    DateTime OccurredAt
) : IEntityDomainEvent
{
    string IEntityDomainEvent.EntityId => UserId;
    string IEntityDomainEvent.EntityType => "User";
}
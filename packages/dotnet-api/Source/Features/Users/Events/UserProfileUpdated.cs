using Source.Shared.Events;

namespace Source.Features.Users.Events;

public record UserProfileUpdated(
    string UserId,
    string? FirstName,
    string? LastName,
    DateTime OccurredAt
) : IEntityDomainEvent
{
    public UserProfileUpdated(string userId, string? firstName, string? lastName)
        : this(userId, firstName, lastName, DateTime.UtcNow) { }

    string IEntityDomainEvent.EntityId => UserId;
    string IEntityDomainEvent.EntityType => "User";
}

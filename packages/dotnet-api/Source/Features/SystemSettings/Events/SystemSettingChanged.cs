using Source.Shared.Events;

namespace Source.Features.SystemSettings.Events;

/// <summary>
/// Raised whenever a <see cref="Models.SystemSetting"/> row is created or updated.
/// The cache invalidator listens for this event and drops the cached entry for
/// <see cref="Category"/> so the next read pulls fresh values from the DB.
/// </summary>
public record SystemSettingChanged(string Key, string Category) : IDomainEvent
{
    public DateTime OccurredAt { get; } = DateTime.UtcNow;
}

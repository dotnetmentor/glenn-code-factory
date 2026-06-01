using Source.Features.SystemSettings.Events;
using Source.Features.SystemSettings.Services;
using Source.Shared.Events;

namespace Source.Features.SystemSettings.EventHandlers;

/// <summary>
/// Drops the cached entries for the affected category whenever a setting changes.
/// Runs after <see cref="ApplicationDbContext"/> commits, courtesy of
/// <see cref="Source.Infrastructure.Interceptors.DomainEventInterceptor"/>.
///
/// <para>Depends on the singleton <see cref="SystemSettingsCache"/> directly — not on
/// the scoped <see cref="ISystemSettingsService"/> — so the handler is itself safe to
/// resolve from any scope and doesn't need a DbContext.</para>
/// </summary>
public class SystemSettingsCacheInvalidator : IEventHandler<SystemSettingChanged>
{
    private readonly SystemSettingsCache _cache;
    private readonly ILogger<SystemSettingsCacheInvalidator> _logger;

    public SystemSettingsCacheInvalidator(
        SystemSettingsCache cache,
        ILogger<SystemSettingsCacheInvalidator> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public Task Handle(SystemSettingChanged notification, CancellationToken cancellationToken)
    {
        _cache.Invalidate(notification.Category);
        _logger.LogDebug("SystemSettings cache invalidated for category {Category} (key {Key})",
            notification.Category, notification.Key);
        return Task.CompletedTask;
    }
}

using Source.Features.RuntimeTokens.Services;
using Source.Features.SystemSettings.Events;
using Source.Shared.Events;

namespace Source.Features.RuntimeTokens.EventHandlers;

/// <summary>
/// Drops the in-memory key cache on <see cref="RuntimeTokenSigningKeyService"/>
/// whenever a <c>RuntimeTokens</c>-category SystemSetting changes — i.e. when an
/// operator rotates Current/Previous via the admin UI. Other categories are ignored
/// (the rest of the app's SystemSettings traffic is irrelevant here).
///
/// <para>Intentionally separate from the service so the service stays a pure
/// singleton: MediatR's assembly-scan registers handlers as transient, and we want
/// the cache to live on a single instance shared across all callers.</para>
/// </summary>
public class RuntimeTokenSigningKeyCacheInvalidator : IEventHandler<SystemSettingChanged>
{
    private readonly IRuntimeTokenSigningKeyService _service;
    private readonly ILogger<RuntimeTokenSigningKeyCacheInvalidator> _logger;

    public RuntimeTokenSigningKeyCacheInvalidator(
        IRuntimeTokenSigningKeyService service,
        ILogger<RuntimeTokenSigningKeyCacheInvalidator> logger)
    {
        _service = service;
        _logger = logger;
    }

    public Task Handle(SystemSettingChanged notification, CancellationToken cancellationToken)
    {
        if (!string.Equals(notification.Category, RuntimeTokenSigningKeyService.Category, StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        // Only the concrete impl exposes the (internal) cache-busting hook. We rely on the
        // contract that DI hands back the singleton concrete type registered in Program.cs.
        if (_service is RuntimeTokenSigningKeyService concrete)
        {
            concrete.InvalidateCache();
            _logger.LogDebug(
                "RuntimeToken signing-key cache invalidated (SystemSetting {Key} changed).",
                notification.Key);
        }

        return Task.CompletedTask;
    }
}

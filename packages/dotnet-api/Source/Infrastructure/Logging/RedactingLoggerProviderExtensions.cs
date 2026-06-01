using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Source.Infrastructure.Logging;

/// <summary>
/// Wires <see cref="RedactingLoggerProvider"/> into the logging pipeline by
/// decorating every <see cref="ILoggerProvider"/> already registered in DI
/// (Console, Debug, EventSource, …). Call this AFTER all
/// <c>builder.Logging.AddX()</c> calls so the wrapping captures every sink.
/// </summary>
public static class RedactingLoggerProviderExtensions
{
    public static IServiceCollection AddJwtRedactingLogging(this IServiceCollection services)
    {
        // Snapshot the current ILoggerProvider registrations, swap each one for
        // a RedactingLoggerProvider that owns the original instance. We resolve
        // the inner provider via the original ImplementationFactory / Type so
        // its own DI dependencies still work.
        var providerDescriptors = services
            .Where(d => d.ServiceType == typeof(ILoggerProvider))
            .ToList();

        foreach (var descriptor in providerDescriptors)
        {
            services.Remove(descriptor);

            services.Add(ServiceDescriptor.Singleton<ILoggerProvider>(sp =>
            {
                ILoggerProvider inner;
                if (descriptor.ImplementationInstance is ILoggerProvider instance)
                {
                    inner = instance;
                }
                else if (descriptor.ImplementationFactory is { } factory)
                {
                    inner = (ILoggerProvider)factory(sp);
                }
                else if (descriptor.ImplementationType is { } type)
                {
                    inner = (ILoggerProvider)ActivatorUtilities.CreateInstance(sp, type);
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Cannot resolve inner ILoggerProvider for descriptor {descriptor}.");
                }

                return new RedactingLoggerProvider(inner);
            }));
        }

        return services;
    }
}

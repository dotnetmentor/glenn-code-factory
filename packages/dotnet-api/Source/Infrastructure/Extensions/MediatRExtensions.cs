using MediatR;
using Source.Shared.Behaviors;
using System.Reflection;

namespace Source.Infrastructure.Extensions;

public static class MediatRExtensions
{
    public static IServiceCollection AddMediatRServices(this IServiceCollection services)
    {
        // Register MediatR with all handlers from the current assembly
        services.AddMediatR(cfg => 
        {
            cfg.RegisterServicesFromAssembly(Assembly.GetExecutingAssembly());

            // Pipeline behavior order (outermost → innermost):
            //   1. TracingBehavior     — starts the OpenTelemetry span that downstream behaviors live in.
            //   2. ErrorCaptureBehavior — catches handler exceptions into the ErrorQueue so they reach the
            //                             persistence pipeline; must run inside Tracing so the captured entry
            //                             carries the same trace context, but outside Logging so the logging
            //                             behavior still observes the exception on its way out.
            //   3. LoggingBehavior      — innermost, closest to the handler; logs and rethrows.
            cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(TracingBehavior<,>));
            cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(ErrorCaptureBehavior<,>));
            cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        });

        return services;
    }
} 
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Source.Infrastructure.ErrorHandling;
using Source.Infrastructure.Extensions;
using Source.Shared.Behaviors;

namespace Api.Tests.Shared.Behaviors;

/// <summary>
/// Guards the MediatR pipeline behavior registration order, which is load-bearing:
///
/// <list type="bullet">
///   <item><see cref="TracingBehavior{TRequest, TResponse}"/> must be OUTERMOST so every
///   captured error and every log line carries its span context.</item>
///   <item><see cref="ErrorCaptureBehavior{TRequest, TResponse}"/> sits in the MIDDLE so
///   exceptions are enqueued WITH trace context, but the logging behavior below still
///   observes the same exception on its way out.</item>
///   <item><see cref="LoggingBehavior{TRequest, TResponse}"/> is INNERMOST, closest to
///   the handler.</item>
/// </list>
///
/// MediatR executes pipeline behaviors in registration order (first registered = outermost),
/// so asserting the registration list order is equivalent to asserting execution order.
/// </summary>
public class MediatRBehaviorOrderingTests
{
    [Fact]
    public void Pipeline_Behaviors_Registered_In_Correct_Order()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ErrorQueue(new PiiRedactor()));
        services.AddLogging();
        services.AddMediatRServices();

        var openBehaviors = services
            .Where(d => d.ServiceType == typeof(IPipelineBehavior<,>))
            .Select(d => d.ImplementationType)
            .ToList();

        openBehaviors.Should().ContainInOrder(
            typeof(TracingBehavior<,>),
            typeof(ErrorCaptureBehavior<,>),
            typeof(LoggingBehavior<,>));

        // Also assert these are the ONLY open-generic pipeline behaviors, so a sneaky
        // additional registration (e.g. a future Validation behavior inserted in the
        // wrong slot) will trigger this test and force an intentional ordering decision.
        openBehaviors.Should().HaveCount(3);
    }
}

using System.Diagnostics;
using MediatR;
using Source.Infrastructure.ErrorHandling;

namespace Source.Shared.Behaviors;

/// <summary>
/// MediatR pipeline behavior that captures unhandled exceptions from downstream
/// handlers into the <see cref="ErrorQueue"/> so that every failure reaches the
/// error-persistence pipeline regardless of whether an HTTP middleware also sees it.
///
/// <para><b>Semantics.</b> try → <c>await next()</c>. On catch: build an
/// <see cref="ErrorEntry"/> with <c>Source="Handler"</c>, <c>ContextData</c> set to the
/// request type name, <c>CorrelationId</c> taken from <see cref="Activity.Current"/>
/// (preferring <c>TraceId</c>, falling back to <c>RootId</c>/<c>Id</c>), enqueue it,
/// and rethrow the ORIGINAL exception via bare <c>throw;</c> to preserve its stack.</para>
///
/// <para><b>Pipeline position.</b> Registered between <see cref="TracingBehavior{TRequest,TResponse}"/>
/// (outermost) and <see cref="LoggingBehavior{TRequest,TResponse}"/> (innermost). This way
/// the captured error already has trace context, and the logging behavior — which logs
/// the exception at Error level before rethrowing — still observes the same exception.</para>
///
/// <para><b>Defense in depth.</b> The production <see cref="ErrorQueue"/> honors a
/// never-throw contract on <c>EnqueueAsync</c>. We still wrap the enqueue call in a
/// try/catch: even a broken error pipeline MUST NOT mask the handler's exception. If
/// the enqueue throws, we log-and-swallow, then rethrow the original — never the second
/// exception.</para>
/// </summary>
/// <typeparam name="TRequest">The MediatR request type.</typeparam>
/// <typeparam name="TResponse">The MediatR response type.</typeparam>
public sealed class ErrorCaptureBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly ErrorQueue _queue;
    private readonly ILogger<ErrorCaptureBehavior<TRequest, TResponse>> _logger;

    public ErrorCaptureBehavior(
        ErrorQueue queue,
        ILogger<ErrorCaptureBehavior<TRequest, TResponse>> logger)
    {
        _queue = queue;
        _logger = logger;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        try
        {
            return await next();
        }
        catch (Exception ex)
        {
            try
            {
                // Prefer the W3C TraceId (a 32-hex-char identifier propagated across
                // service boundaries). Fall back to RootId / Id for cases where an
                // Activity exists but has no trace context (e.g. legacy propagation).
                // Null is an acceptable value when no Activity is in flight.
                string? correlationId = null;
                var current = Activity.Current;
                if (current is not null)
                {
                    var traceId = current.TraceId.ToString();
                    correlationId = traceId != "00000000000000000000000000000000"
                        ? traceId
                        : current.RootId ?? current.Id;
                }

                var entry = new ErrorEntry(
                    Message: ex.Message,
                    StackTrace: ex.StackTrace,
                    Source: "Handler",
                    Severity: "Error",
                    CorrelationId: correlationId,
                    RequestPath: null,
                    RequestMethod: null,
                    ContextData: typeof(TRequest).Name,
                    OccurredAt: DateTime.UtcNow);

                await _queue.EnqueueAsync(entry);
            }
            catch (Exception enqueueEx)
            {
                // Defense in depth. ErrorQueue.EnqueueAsync is contractually never-throw,
                // but an injected broken subclass / construction-time misconfiguration
                // could still propagate. Under no circumstance may we mask the handler's
                // exception with an error-pipeline failure.
                _logger.LogError(
                    enqueueEx,
                    "Failed to enqueue handler error for {RequestType} — original exception will still propagate",
                    typeof(TRequest).Name);
            }

            // Bare throw preserves the original stack trace. Never use `throw ex;`.
            throw;
        }
    }
}

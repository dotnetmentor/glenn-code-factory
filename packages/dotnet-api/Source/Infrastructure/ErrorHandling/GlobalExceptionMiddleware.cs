using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace Source.Infrastructure.ErrorHandling;

/// <summary>
/// Middleware that catches all unhandled exceptions in the HTTP pipeline,
/// enqueues them for persistence, and returns a consistent JSON error response.
/// </summary>
public class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ErrorQueue _errorQueue;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public GlobalExceptionMiddleware(RequestDelegate next, ErrorQueue errorQueue, ILogger<GlobalExceptionMiddleware> logger)
    {
        _next = next;
        _errorQueue = errorQueue;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);

            // After pipeline — log interesting HTTP status codes (non-exception responses)
            if (context.Response.StatusCode == 403
                || context.Response.StatusCode == 404
                || context.Response.StatusCode >= 500)
            {
                var severity = context.Response.StatusCode >= 500 ? "Error" : "Warning";
                var correlationId = Activity.Current?.Id ?? context.TraceIdentifier;

                try
                {
                    await _errorQueue.EnqueueAsync(new ErrorEntry(
                        Message: $"HTTP {context.Response.StatusCode} {GetStatusDescription(context.Response.StatusCode)} — {context.Request.Method} {context.Request.Path}",
                        StackTrace: null,
                        Source: "HTTP",
                        Severity: severity,
                        CorrelationId: correlationId,
                        RequestPath: context.Request.Path,
                        RequestMethod: context.Request.Method,
                        ContextData: context.Request.QueryString.HasValue ? context.Request.QueryString.Value : null,
                        OccurredAt: DateTime.UtcNow
                    ));
                }
                catch (Exception enqueueEx)
                {
                    _logger.LogError(enqueueEx, "Failed to enqueue HTTP status error entry for {StatusCode}", context.Response.StatusCode);
                }
            }
        }
        catch (Exception ex)
        {
            var correlationId = Activity.Current?.Id ?? Guid.NewGuid().ToString();

            _logger.LogError(ex, "Unhandled exception caught by GlobalExceptionMiddleware. CorrelationId: {CorrelationId}", correlationId);

            var severity = ex is OutOfMemoryException or StackOverflowException or AccessViolationException
                ? "Critical"
                : "Error";

            var errorEntry = new ErrorEntry(
                Message: ex.Message,
                StackTrace: ex.StackTrace,
                Source: "HTTP",
                Severity: severity,
                CorrelationId: correlationId,
                RequestPath: context.Request.Path.ToString(),
                RequestMethod: context.Request.Method,
                ContextData: null,
                OccurredAt: DateTime.UtcNow
            );

            try
            {
                await _errorQueue.EnqueueAsync(errorEntry);
            }
            catch (Exception enqueueEx)
            {
                _logger.LogError(enqueueEx, "Failed to enqueue error entry");
            }

            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            context.Response.ContentType = "application/json";

            var responseBody = new
            {
                error = "An unexpected error occurred.",
                correlationId
            };

            await context.Response.WriteAsync(JsonSerializer.Serialize(responseBody, JsonOptions));
        }
    }

    private static string GetStatusDescription(int statusCode) => statusCode switch
    {
        403 => "Forbidden",
        404 => "Not Found",
        500 => "Internal Server Error",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        504 => "Gateway Timeout",
        _ => "Error"
    };
}

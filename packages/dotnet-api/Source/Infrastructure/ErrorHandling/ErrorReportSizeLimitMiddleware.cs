namespace Source.Infrastructure.ErrorHandling;

/// <summary>
/// Tiny middleware that enforces an 8 KB hard cap on the public error-report endpoint
/// and returns a clean <c>413 Payload Too Large</c> response BEFORE MVC's model binding
/// runs.
///
/// <para><b>Why not <see cref="Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute"/> alone?</b>
/// That attribute is enforced by Kestrel's <c>IHttpMaxRequestBodySizeFeature</c>, which
/// throws <c>BadHttpRequestException</c> during body-read from inside the model binder.
/// MVC's default model-state filter catches that as a bad-request (400), not a
/// payload-too-large (413). The spec explicitly requires 413 on oversize, so we peek
/// at <c>Content-Length</c> up-front and short-circuit with the right status.</para>
///
/// <para>Scoped to the error-report endpoint only — any other endpoint can set its own
/// request-size limit however it likes.</para>
/// </summary>
public sealed class ErrorReportSizeLimitMiddleware
{
    private const string ReportPath = "/api/errors/report";
    internal const long MaxBytes = 8 * 1024;

    private readonly RequestDelegate _next;

    public ErrorReportSizeLimitMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments(ReportPath, StringComparison.OrdinalIgnoreCase))
        {
            var contentLength = context.Request.ContentLength;
            if (contentLength is not null && contentLength > MaxBytes)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }
        }

        await _next(context);
    }
}

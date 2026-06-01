using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Source.Infrastructure.ErrorHandling;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace Source.Features.ErrorLog.Controllers;

/// <summary>
/// Public, anonymous endpoint that accepts frontend error reports and enqueues them
/// into the existing <see cref="ErrorQueue"/>. This is Phase 3 of the resilient error
/// capture pipeline spec: the browser can ship errors without auth, but every layer
/// assumes the input is hostile.
///
/// <para><b>Hardening contract:</b></para>
/// <list type="bullet">
/// <item><b>8 KB body cap</b> via <see cref="RequestSizeLimitAttribute"/> — Kestrel rejects
///     with 413 before the action runs.</item>
/// <item><b>Per-IP rate limit</b> via the <c>ErrorReport</c> policy (10/sec burst); on
///     rejection the handler returns 204 silently, never 429, so an attacker gets no
///     feedback signal.</item>
/// <item><b>Strict validation</b> via <see cref="ErrorReportRequest"/> DataAnnotations —
///     missing or oversized fields auto-return 400.</item>
/// <item><b>Server-forced Severity/Source</b> — whatever the client sent for those fields
///     is ignored; the enqueued entry is always <c>Severity=Error</c>, <c>Source=Frontend</c>.
///     Clients cannot self-declare "Critical".</item>
/// <item><b>PII redaction</b> happens inside <see cref="ErrorQueue.EnqueueAsync"/> on the
///     single hot path, so nothing the client submits ever lands in the DB unredacted.</item>
/// </list>
/// </summary>
[ApiController]
[Route("api/errors")]
[Tags("ErrorReport")]
public sealed class ErrorReportController : ControllerBase
{
    private readonly ErrorQueue _queue;

    public ErrorReportController(ErrorQueue queue)
    {
        _queue = queue;
    }

    [HttpPost("report")]
    [AllowAnonymous]
    [RequestSizeLimit(8192)]
    [EnableRateLimiting("ErrorReport")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult> Report([FromBody] ErrorReportRequest request)
    {
        // Server-forced fields: clients must NEVER be able to self-declare the severity
        // or source of a report. A compromised frontend could otherwise flood the dashboard
        // with `Critical` rows and mask real incidents.
        var entry = new ErrorEntry(
            Message: request.Message,
            StackTrace: request.StackTrace,
            Source: "Frontend",
            Severity: "Error",
            CorrelationId: request.CorrelationId,
            RequestPath: request.Url,
            RequestMethod: null,
            ContextData: BuildContextData(request),
            OccurredAt: DateTime.UtcNow);

        await _queue.EnqueueAsync(entry);

        return NoContent();
    }

    private static string? BuildContextData(ErrorReportRequest r)
    {
        // Keep the optional frontend-only metadata (user agent, source location, error type)
        // out of first-class columns and pack them into the free-form ContextData field so
        // the error table schema stays stable.
        if (string.IsNullOrWhiteSpace(r.UserAgent)
            && r.LineNumber is null
            && r.ColumnNumber is null
            && string.IsNullOrWhiteSpace(r.ErrorType))
        {
            return null;
        }

        return JsonSerializer.Serialize(new
        {
            r.UserAgent,
            r.LineNumber,
            r.ColumnNumber,
            r.ErrorType,
        });
    }
}

/// <summary>
/// Payload accepted by <see cref="ErrorReportController.Report"/>. Every field is
/// length-capped because this endpoint is anonymous and public; a missing
/// <see cref="Message"/> or an oversized field auto-returns 400 via MVC's default
/// model-state filter.
///
/// <para>Note that <c>Severity</c> and <c>Source</c> are intentionally <b>absent</b> from
/// this DTO: the server forces them. Any fields the client adds beyond these are
/// ignored by System.Text.Json.</para>
/// </summary>
public sealed record ErrorReportRequest
{
    [Required]
    [StringLength(1000)]
    public string Message { get; init; } = string.Empty;

    [StringLength(4000)]
    public string? StackTrace { get; init; }

    [StringLength(500)]
    public string? Url { get; init; }

    [StringLength(500)]
    public string? UserAgent { get; init; }

    [StringLength(100)]
    public string? CorrelationId { get; init; }

    [StringLength(100)]
    public string? ErrorType { get; init; }

    public int? LineNumber { get; init; }

    public int? ColumnNumber { get; init; }
}

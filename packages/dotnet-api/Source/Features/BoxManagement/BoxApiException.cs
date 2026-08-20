namespace Source.Features.BoxManagement;

/// <summary>
/// Typed exception for non-success responses from the Box API (box.ascii.dev).
/// Carries the HTTP status, the parsed error code (when Box returns one in the
/// body), and the raw body for diagnostics.
///
/// <para>We throw this instead of letting the raw <see cref="HttpRequestException"/>
/// surface so handlers up the stack can switch on <see cref="StatusCode"/> /
/// <see cref="ErrorCode"/> for retry-vs-give-up decisions without parsing bodies
/// again. Two Box codes matter operationally: <c>box_starting</c> (409 — the box
/// is mid-provision/resume, retry shortly) and <c>machine_not_running</c> (the
/// box must be resumed before commands land). <see cref="IsRetriableStartup"/>
/// folds both into one check.</para>
/// </summary>
public class BoxApiException : Exception
{
    /// <summary>HTTP status code returned by Box (e.g. 404, 409, 429, 500).</summary>
    public int StatusCode { get; }

    /// <summary>Optional machine-readable error code parsed from the JSON body (e.g. <c>box_starting</c>, <c>rate_limited</c>, <c>daily_limit_reached</c>).</summary>
    public string? ErrorCode { get; }

    /// <summary>Raw response body, captured before any deserialisation. Bounded in length by callers.</summary>
    public string Body { get; }

    /// <summary>
    /// True when the failure means "the box isn't up yet / not running" — a
    /// transient startup condition the caller should retry after a short delay
    /// rather than treat as a hard failure.
    /// </summary>
    public bool IsRetriableStartup =>
        ErrorCode is "box_starting" or "machine_not_running"
        || StatusCode == 409;

    public BoxApiException(int statusCode, string? errorCode, string body, string message)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
        Body = body;
    }
}

namespace Source.Features.BoxManagement;

/// <summary>
/// Typed exception for non-success responses from the Box API (box.ascii.dev).
/// Carries the HTTP status, the parsed error code (when Box returns one in the
/// body), the request id from the error envelope, and the raw body for
/// diagnostics.
///
/// <para>Box's ErrorEnvelope is
/// <c>{ "ok": false, "type": "box.error", "status": int, "code": string,
/// "message": string, "error": { code, message, status, details? },
/// "requestId": string }</c> — the code is extracted preferring the top-level
/// <c>code</c>, then <c>error.code</c>.</para>
///
/// <para>We throw this instead of letting the raw <see cref="HttpRequestException"/>
/// surface so handlers up the stack can switch on <see cref="StatusCode"/> /
/// <see cref="ErrorCode"/> for retry-vs-give-up decisions without parsing bodies
/// again. Operationally interesting codes: <c>box_starting</c> /
/// <c>machine_not_running</c> / <c>idempotency_in_progress</c> (transient — retry
/// shortly; see <see cref="IsRetriableStartup"/>) and the 429 rate-limit pair
/// <c>rate_limited</c> / <c>start_limit_reached</c>. Other common codes:
/// <c>unauthorized</c>, <c>forbidden</c>, <c>invalid_json</c>,
/// <c>provider_not_configured</c>, <c>unknown_environment</c>,
/// <c>type_too_small</c>.</para>
/// </summary>
public class BoxApiException : Exception
{
    /// <summary>HTTP status code returned by Box (e.g. 404, 409, 429, 500).</summary>
    public int StatusCode { get; }

    /// <summary>Optional machine-readable error code parsed from the JSON body (e.g. <c>box_starting</c>, <c>rate_limited</c>, <c>start_limit_reached</c>).</summary>
    public string? ErrorCode { get; }

    /// <summary>Raw response body, captured before any deserialisation. Bounded in length by callers.</summary>
    public string Body { get; }

    /// <summary>Box's <c>requestId</c> from the error envelope, for support correlation. Null when the body carried none.</summary>
    public string? RequestId { get; }

    /// <summary>
    /// True when the failure means "the box isn't up yet / not running / a
    /// concurrent identical call is still in flight" — a transient startup
    /// condition the caller should retry after a short delay rather than treat
    /// as a hard failure.
    /// </summary>
    public bool IsRetriableStartup =>
        ErrorCode is "box_starting" or "machine_not_running" or "idempotency_in_progress"
        || StatusCode == 409;

    /// <summary>
    /// True for the contract's 429 rate-limit codes (<c>rate_limited</c>,
    /// <c>start_limit_reached</c>) or a bare 429.
    /// </summary>
    public bool IsRateLimited =>
        StatusCode == 429
        || ErrorCode is "rate_limited" or "start_limit_reached";

    public BoxApiException(int statusCode, string? errorCode, string body, string message, string? requestId = null)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
        Body = body;
        RequestId = requestId;
    }
}

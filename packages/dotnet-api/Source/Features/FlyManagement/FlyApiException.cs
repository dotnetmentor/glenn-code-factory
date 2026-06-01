namespace Source.Features.FlyManagement;

/// <summary>
/// Typed exception for non-success responses from the Fly Machines API. Carries the
/// HTTP status, the parsed error code (when Fly returns one in the body), the request
/// ID (from the <c>fly-request-id</c> header — invaluable when opening a support ticket),
/// and the raw body for diagnostics.
///
/// <para>We throw this instead of letting the raw <see cref="HttpRequestException"/>
/// surface so handlers up the stack can switch on <see cref="StatusCode"/> /
/// <see cref="ErrorCode"/> for retry-vs-give-up decisions without parsing bodies again.</para>
/// </summary>
public class FlyApiException : Exception
{
    /// <summary>HTTP status code returned by Fly (e.g. 404, 422, 429, 500).</summary>
    public int StatusCode { get; }

    /// <summary>Optional machine-readable error code parsed from the JSON body, if Fly provided one.</summary>
    public string? ErrorCode { get; }

    /// <summary>Value of the <c>fly-request-id</c> response header, if present. Use when contacting Fly support.</summary>
    public string? RequestId { get; }

    /// <summary>Raw response body, captured before any deserialisation. Bounded in length by callers.</summary>
    public string Body { get; }

    public FlyApiException(int statusCode, string? errorCode, string? requestId, string body, string message)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
        RequestId = requestId;
        Body = body;
    }
}

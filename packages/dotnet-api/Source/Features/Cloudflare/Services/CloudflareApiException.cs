namespace Source.Features.Cloudflare.Services;

/// <summary>
/// Surface for any non-2xx response from the Cloudflare API or transport-level
/// failure mid-batch. Carries the raw status code + body so the batch handler
/// can log richly without re-parsing.
/// </summary>
public class CloudflareApiException : Exception
{
    public int StatusCode { get; }
    public string Body { get; }

    public CloudflareApiException(int statusCode, string body, string message)
        : base(message)
    {
        StatusCode = statusCode;
        Body = body;
    }

    public CloudflareApiException(int statusCode, string body, string message, Exception inner)
        : base(message, inner)
    {
        StatusCode = statusCode;
        Body = body;
    }
}

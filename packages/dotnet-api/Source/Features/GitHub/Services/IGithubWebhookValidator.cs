namespace Source.Features.GitHub.Services;

/// <summary>
/// Verifies the <c>X-Hub-Signature-256</c> header that GitHub attaches to each webhook
/// delivery. Backed by HMAC-SHA256 over the raw request body using the App's webhook
/// secret (configured under <c>GitHub:WebhookSecret</c>).
/// </summary>
public interface IGithubWebhookValidator
{
    /// <summary>
    /// Returns true iff <paramref name="signatureHeader"/> equals
    /// <c>"sha256="+ HMAC-SHA256(body, secret)</c> using a timing-safe comparison.
    /// Returns false on any malformed/missing header.
    /// </summary>
    bool Validate(string? signatureHeader, byte[] body);
}

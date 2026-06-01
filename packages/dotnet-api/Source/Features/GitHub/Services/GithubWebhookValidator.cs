using System.Security.Cryptography;
using System.Text;
using Source.Features.GitHub.Configuration;

namespace Source.Features.GitHub.Services;

/// <summary>
/// Default implementation of <see cref="IGithubWebhookValidator"/>.
/// Uses <see cref="CryptographicOperations.FixedTimeEquals(System.ReadOnlySpan{byte},System.ReadOnlySpan{byte})"/>
/// to defend against timing-attack-driven secret recovery.
/// </summary>
public class GithubWebhookValidator : IGithubWebhookValidator
{
    private const string Prefix = "sha256=";
    private readonly IGithubOptionsAccessor _options;

    public GithubWebhookValidator(IGithubOptionsAccessor options)
    {
        _options = options;
    }

    public bool Validate(string? signatureHeader, byte[] body)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader)) return false;
        if (!signatureHeader.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var secret = _options.Current.WebhookSecret;
        if (string.IsNullOrEmpty(secret)) return false;
        if (body is null) return false;

        var providedHex = signatureHeader.Substring(Prefix.Length).Trim();
        if (providedHex.Length == 0) return false;

        // Convert.FromHexString throws on odd length / non-hex characters; treat any
        // such header as a hard fail rather than a 500.
        byte[] providedBytes;
        try
        {
            providedBytes = Convert.FromHexString(providedHex);
        }
        catch (FormatException)
        {
            return false;
        }

        var keyBytes = Encoding.UTF8.GetBytes(secret);
        using var hmac = new HMACSHA256(keyBytes);
        var computed = hmac.ComputeHash(body);

        // Length-mismatch is itself a fast-fail before the timing-safe compare.
        if (providedBytes.Length != computed.Length) return false;

        return CryptographicOperations.FixedTimeEquals(providedBytes, computed);
    }
}

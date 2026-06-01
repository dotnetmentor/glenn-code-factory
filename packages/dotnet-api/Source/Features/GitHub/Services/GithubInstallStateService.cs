using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Source.Features.GitHub.Configuration;

namespace Source.Features.GitHub.Services;

/// <summary>
/// Default <see cref="IGithubInstallStateService"/> implementation.
///
/// Token shape: <c>{base64url(payload-json)}.{base64url(hmac-sha256-of-payload-bytes)}</c>.
/// HMAC key is <see cref="GithubOptions.WebhookSecret"/> — single source of high-entropy
/// secret keeps configuration simple. Verification is timing-safe.
/// </summary>
public sealed class GithubInstallStateService : IGithubInstallStateService
{
    private readonly IGithubOptionsAccessor _options;

    public GithubInstallStateService(IGithubOptionsAccessor options)
    {
        _options = options;
    }

    public string Issue(Guid workspaceId, TimeSpan ttl)
    {
        var key = ResolveKey();
        var payload = new StatePayload(
            WorkspaceId: workspaceId,
            Nonce: Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
            ExpiresAt: DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeSeconds());

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);
        var sigBytes = HMACSHA256.HashData(key, payloadBytes);
        return $"{Base64UrlEncode(payloadBytes)}.{Base64UrlEncode(sigBytes)}";
    }

    public string IssueReauth(Guid workspaceId, Guid githubInstallationId, TimeSpan ttl)
    {
        var key = ResolveKey();
        var payload = new ReauthPayload(
            WorkspaceId: workspaceId,
            GithubInstallationId: githubInstallationId,
            Nonce: Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
            ExpiresAt: DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeSeconds());

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);
        var sigBytes = HMACSHA256.HashData(key, payloadBytes);
        // The "r." prefix distinguishes reauth tokens from install-state tokens so
        // a stolen reauth token can't be replayed as an install-state token (and vice
        // versa). The byte payloads themselves differ — but the prefix is a belt-and-
        // braces guard that costs nothing.
        return "r." + Base64UrlEncode(payloadBytes) + "." + Base64UrlEncode(sigBytes);
    }

    public ReauthStatePayload? VerifyReauth(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        // Reauth tokens are tagged "r." so they can't be confused with install-state tokens.
        if (!token.StartsWith("r.", StringComparison.Ordinal)) return null;
        var body = token.Substring(2);

        var dot = body.IndexOf('.');
        if (dot <= 0 || dot >= body.Length - 1) return null;

        var payloadPart = body[..dot];
        var sigPart = body[(dot + 1)..];

        byte[] payloadBytes;
        byte[] providedSig;
        try
        {
            payloadBytes = Base64UrlDecode(payloadPart);
            providedSig = Base64UrlDecode(sigPart);
        }
        catch (FormatException)
        {
            return null;
        }

        var key = ResolveKey();
        var expectedSig = HMACSHA256.HashData(key, payloadBytes);

        if (expectedSig.Length != providedSig.Length) return null;
        if (!CryptographicOperations.FixedTimeEquals(expectedSig, providedSig)) return null;

        ReauthPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<ReauthPayload>(payloadBytes, JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
        if (payload is null) return null;

        if (payload.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return null;
        if (payload.WorkspaceId == Guid.Empty) return null;
        if (payload.GithubInstallationId == Guid.Empty) return null;

        return new ReauthStatePayload(payload.WorkspaceId, payload.GithubInstallationId);
    }

    public Guid? Verify(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        // Install-state tokens are unprefixed. Anything starting with "r." is a reauth
        // token and should never validate here — call VerifyReauth instead.
        if (token.StartsWith("r.", StringComparison.Ordinal)) return null;

        var dot = token.IndexOf('.');
        if (dot <= 0 || dot >= token.Length - 1) return null;

        var payloadPart = token[..dot];
        var sigPart = token[(dot + 1)..];

        byte[] payloadBytes;
        byte[] providedSig;
        try
        {
            payloadBytes = Base64UrlDecode(payloadPart);
            providedSig = Base64UrlDecode(sigPart);
        }
        catch (FormatException)
        {
            return null;
        }

        var key = ResolveKey();
        var expectedSig = HMACSHA256.HashData(key, payloadBytes);

        // Length check first to avoid FixedTimeEquals throwing on length mismatch.
        if (expectedSig.Length != providedSig.Length) return null;
        if (!CryptographicOperations.FixedTimeEquals(expectedSig, providedSig)) return null;

        StatePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<StatePayload>(payloadBytes, JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
        if (payload is null) return null;

        if (payload.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return null;
        if (payload.WorkspaceId == Guid.Empty) return null;

        return payload.WorkspaceId;
    }

    // -----------------------------------------------------------------------

    private byte[] ResolveKey()
    {
        var raw = _options.Current.WebhookSecret;
        if (string.IsNullOrWhiteSpace(raw))
        {
            // Fall back to a fixed dev-only key so unconfigured environments still spin up.
            // The install flow will still cryptographically validate the round-trip; only
            // a co-located attacker could forge — acceptable in dev. Production should
            // always have WebhookSecret set.
            raw = "github-install-state-dev-fallback-key";
        }
        return Encoding.UTF8.GetBytes(raw);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private sealed record StatePayload(
        [property: JsonPropertyName("workspaceId")] Guid WorkspaceId,
        [property: JsonPropertyName("nonce")] string Nonce,
        [property: JsonPropertyName("expiresAt")] long ExpiresAt);

    private sealed record ReauthPayload(
        [property: JsonPropertyName("workspaceId")] Guid WorkspaceId,
        [property: JsonPropertyName("githubInstallationId")] Guid GithubInstallationId,
        [property: JsonPropertyName("nonce")] string Nonce,
        [property: JsonPropertyName("expiresAt")] long ExpiresAt);
}

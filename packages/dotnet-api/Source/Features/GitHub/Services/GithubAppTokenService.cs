using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;
using Source.Features.GitHub.Configuration;

namespace Source.Features.GitHub.Services;

/// <summary>
/// Default implementation. Uses <c>System.IdentityModel.Tokens.Jwt</c> (already a
/// transitive dep via <c>Microsoft.AspNetCore.Authentication.JwtBearer</c>) to stay
/// consistent with <see cref="Source.Features.Authentication.Services.JwtTokenService"/>.
/// </summary>
public class GithubAppTokenService : IGithubAppTokenService
{
    private const string CacheKeyPrefix = "github:install-token:";
    private const string ScopedCacheKeyPrefix = "github:scoped-token:";

    private readonly IGithubOptionsAccessor _options;
    private readonly IMemoryCache _cache;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GithubAppTokenService> _logger;

    public GithubAppTokenService(
        IGithubOptionsAccessor options,
        IMemoryCache cache,
        IHttpClientFactory httpClientFactory,
        ILogger<GithubAppTokenService> logger)
    {
        _options = options;
        _cache = cache;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string CreateAppJwt()
    {
        var options = _options.Current;
        if (string.IsNullOrWhiteSpace(options.PrivateKeyPem))
        {
            throw new InvalidOperationException("GitHub:PrivateKeyPem is not configured.");
        }
        if (string.IsNullOrWhiteSpace(options.AppId))
        {
            throw new InvalidOperationException("GitHub:AppId is not configured.");
        }

        var rsa = RSA.Create();
        rsa.ImportFromPem(options.PrivateKeyPem);

        // GitHub's docs say: iat 60s in the past (clock-skew safety),
        // exp 10 min in the future. We subtract 60s from exp as a hedge against
        // clock skew on their end too — net = ~9 min usable lifetime.
        var now = DateTimeOffset.UtcNow;
        var iat = now.AddSeconds(-60).ToUnixTimeSeconds();
        var exp = now.AddMinutes(10).AddSeconds(-60).ToUnixTimeSeconds();

        var key = new RsaSecurityKey(rsa) { KeyId = options.AppId };
        var credentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256)
        {
            // Don't dispose the RSA — the SecurityTokenDescriptor below references it
            // for the duration of WriteToken.
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
        };

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.CreateJwtSecurityToken(
            issuer: options.AppId,
            audience: null,
            subject: new ClaimsIdentity(),
            notBefore: null,
            expires: DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime,
            issuedAt: DateTimeOffset.FromUnixTimeSeconds(iat).UtcDateTime,
            signingCredentials: credentials);

        return handler.WriteToken(jwt);
    }

    public async Task<string> GetInstallationTokenAsync(long installationId, CancellationToken ct = default)
    {
        var cacheKey = CacheKeyPrefix + installationId;
        if (_cache.TryGetValue<string>(cacheKey, out var cached) && !string.IsNullOrEmpty(cached))
        {
            return cached;
        }

        var appJwt = CreateAppJwt();
        var client = _httpClientFactory.CreateClient("github");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"app/installations/{installationId}/access_tokens");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", appJwt);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var resp = await client.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogError("Failed to mint GitHub installation token for {InstallationId}: {Status} {Body}", installationId, (int)resp.StatusCode, body);
            throw new HttpRequestException($"GitHub access_tokens call failed: {(int)resp.StatusCode}");
        }

        var payload = await resp.Content.ReadFromJsonAsync<InstallationTokenResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("GitHub returned an empty access_tokens payload.");

        if (string.IsNullOrEmpty(payload.Token))
        {
            throw new InvalidOperationException("GitHub access_tokens response did not include a token.");
        }

        // Cache for min(50 min, expires_at - 5 min) to stay safely under the 1h cap.
        var ttl = TimeSpan.FromMinutes(50);
        if (payload.ExpiresAt is { } expiresAt)
        {
            var safeWindow = expiresAt.ToUniversalTime() - DateTime.UtcNow - TimeSpan.FromMinutes(5);
            if (safeWindow < ttl) ttl = safeWindow;
        }
        if (ttl < TimeSpan.FromMinutes(1)) ttl = TimeSpan.FromMinutes(1);

        _cache.Set(cacheKey, payload.Token, ttl);
        return payload.Token;
    }

    public async Task<ScopedInstallationToken> MintScopedTokenAsync(
        long installationId,
        long repositoryId,
        CancellationToken ct = default)
    {
        var cacheKey = $"{ScopedCacheKeyPrefix}{installationId}:{repositoryId}";
        if (_cache.TryGetValue<ScopedInstallationToken>(cacheKey, out var cached)
            && cached is not null
            && !string.IsNullOrEmpty(cached.Token)
            && cached.ExpiresAt - DateTime.UtcNow > TimeSpan.FromMinutes(1))
        {
            return cached;
        }

        var appJwt = CreateAppJwt();
        var client = _httpClientFactory.CreateClient("github");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"app/installations/{installationId}/access_tokens");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", appJwt);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Content = JsonContent.Create(new ScopedTokenRequest(
            RepositoryIds: new[] { repositoryId },
            Permissions: new ScopedTokenPermissions(Contents: "write", Metadata: "read")));

        using var resp = await client.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogError(
                "Failed to mint scoped GitHub installation token for installation {InstallationId} repo {RepositoryId}: {Status} {Body}",
                installationId, repositoryId, (int)resp.StatusCode, body);
            throw new HttpRequestException($"GitHub access_tokens (scoped) call failed: {(int)resp.StatusCode}");
        }

        var payload = await resp.Content.ReadFromJsonAsync<InstallationTokenResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("GitHub returned an empty access_tokens payload.");

        if (string.IsNullOrEmpty(payload.Token))
        {
            throw new InvalidOperationException("GitHub access_tokens response did not include a token.");
        }

        // GitHub always returns expires_at on this endpoint, but treat absence
        // as a 1h default (the documented cap) so we never end up caching a
        // record with a default(DateTime) sentinel.
        var expiresAt = payload.ExpiresAt?.ToUniversalTime() ?? DateTime.UtcNow.AddHours(1);

        // Same TTL clamping pattern as GetInstallationTokenAsync: aim for 50
        // min, never more than expires_at - 5 min, never less than 1 min.
        var ttl = TimeSpan.FromMinutes(50);
        var safeWindow = expiresAt - DateTime.UtcNow - TimeSpan.FromMinutes(5);
        if (safeWindow < ttl) ttl = safeWindow;
        if (ttl < TimeSpan.FromMinutes(1)) ttl = TimeSpan.FromMinutes(1);

        var scoped = new ScopedInstallationToken(payload.Token, expiresAt);
        _cache.Set(cacheKey, scoped, ttl);

        _logger.LogInformation(
            "Minted scoped GitHub installation token for installation {InstallationId} repo {RepositoryId} (expires {ExpiresAt:O}).",
            installationId, repositoryId, expiresAt);

        return scoped;
    }

    public async Task<ScopedInstallationToken> MintScopedTokenByNameAsync(
        long installationId,
        string repoFullName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repoFullName))
        {
            throw new ArgumentException("repoFullName is required.", nameof(repoFullName));
        }

        // GitHub's access_tokens endpoint expects the *name only* (NOT "owner/name")
        // in the `repositories` array when scoped to a single installation — the
        // installation already binds the owner. Strip any "owner/" prefix the
        // caller passes in for ergonomic parity with the id-based overload.
        var slash = repoFullName.IndexOf('/');
        var repoName = slash >= 0 ? repoFullName[(slash + 1)..] : repoFullName;
        if (string.IsNullOrWhiteSpace(repoName))
        {
            throw new ArgumentException(
                $"repoFullName '{repoFullName}' did not contain a repo name after the slash.",
                nameof(repoFullName));
        }

        // Cache key uses the normalised name (owner/name) so two callers that
        // pass the same logical repo don't double-mint.
        var cacheKey = $"{ScopedCacheKeyPrefix}{installationId}:name:{repoFullName.ToLowerInvariant()}";
        if (_cache.TryGetValue<ScopedInstallationToken>(cacheKey, out var cached)
            && cached is not null
            && !string.IsNullOrEmpty(cached.Token)
            && cached.ExpiresAt - DateTime.UtcNow > TimeSpan.FromMinutes(1))
        {
            return cached;
        }

        var appJwt = CreateAppJwt();
        var client = _httpClientFactory.CreateClient("github");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"app/installations/{installationId}/access_tokens");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", appJwt);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Content = JsonContent.Create(new ScopedTokenByNameRequest(
            Repositories: new[] { repoName },
            Permissions: new ScopedTokenPermissions(Contents: "write", Metadata: "read")));

        using var resp = await client.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogError(
                "Failed to mint scoped GitHub installation token (by name) for installation {InstallationId} repo {RepoFullName}: {Status} {Body}",
                installationId, repoFullName, (int)resp.StatusCode, body);
            throw new HttpRequestException($"GitHub access_tokens (scoped by name) call failed: {(int)resp.StatusCode}");
        }

        var payload = await resp.Content.ReadFromJsonAsync<InstallationTokenResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("GitHub returned an empty access_tokens payload.");

        if (string.IsNullOrEmpty(payload.Token))
        {
            throw new InvalidOperationException("GitHub access_tokens response did not include a token.");
        }

        var expiresAt = payload.ExpiresAt?.ToUniversalTime() ?? DateTime.UtcNow.AddHours(1);

        var ttl = TimeSpan.FromMinutes(50);
        var safeWindow = expiresAt - DateTime.UtcNow - TimeSpan.FromMinutes(5);
        if (safeWindow < ttl) ttl = safeWindow;
        if (ttl < TimeSpan.FromMinutes(1)) ttl = TimeSpan.FromMinutes(1);

        var scoped = new ScopedInstallationToken(payload.Token, expiresAt);
        _cache.Set(cacheKey, scoped, ttl);

        _logger.LogInformation(
            "Minted scoped GitHub installation token (by name) for installation {InstallationId} repo {RepoFullName} (expires {ExpiresAt:O}).",
            installationId, repoFullName, expiresAt);

        return scoped;
    }

    /// <summary>Subset of GitHub's <c>POST /app/installations/{id}/access_tokens</c> response.</summary>
    private sealed record InstallationTokenResponse(
        [property: JsonPropertyName("token")] string Token,
        [property: JsonPropertyName("expires_at")] DateTime? ExpiresAt);

    /// <summary>Body for the repository-scoped variant of access_tokens (by id).</summary>
    private sealed record ScopedTokenRequest(
        [property: JsonPropertyName("repository_ids")] long[] RepositoryIds,
        [property: JsonPropertyName("permissions")] ScopedTokenPermissions Permissions);

    /// <summary>
    /// Body for the repository-scoped variant of access_tokens (by name). GitHub
    /// accepts either <c>repository_ids</c> or <c>repositories</c>; this is the
    /// names variant so we don't need a numeric id from a local cache to mint.
    /// </summary>
    private sealed record ScopedTokenByNameRequest(
        [property: JsonPropertyName("repositories")] string[] Repositories,
        [property: JsonPropertyName("permissions")] ScopedTokenPermissions Permissions);

    private sealed record ScopedTokenPermissions(
        [property: JsonPropertyName("contents")] string Contents,
        [property: JsonPropertyName("metadata")] string Metadata);
}

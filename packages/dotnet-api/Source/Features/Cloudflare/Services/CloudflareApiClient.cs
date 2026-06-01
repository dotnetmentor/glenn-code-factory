using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Source.Features.Cloudflare.Configuration;

namespace Source.Features.Cloudflare.Services;

/// <summary>
/// Thin typed <see cref="HttpClient"/> wrapper for the four Cloudflare API
/// endpoints the preview-tunnel pool actually needs: create tunnel, configure
/// public hostname (ingress), create CNAME, delete tunnel.
///
/// <para>Authoritative reference for the wire shapes is Cloudflare's API
/// docs (<c>api.cloudflare.com</c>) — every endpoint returns the standard
/// <c>{ result, success, errors, messages }</c> envelope which
/// <see cref="UnwrapResultAsync"/> peels off.</para>
///
/// <para><b>Mirrors the FlyClient pattern.</b> Concrete class (no
/// <c>ICloudflareApiClient</c> abstraction), typed HttpClient bound to
/// <c>https://api.cloudflare.com/client/v4/</c>, Bearer auth stamped per-request
/// from <see cref="ICloudflareOptionsAccessor"/> so token rotation in
/// SystemSettings takes effect without a process restart, snake_case JSON
/// serialisation (Cloudflare's wire convention).</para>
///
/// <para><b>No audit table.</b> Unlike FlyClient (which writes one
/// <c>FlyOperation</c> row per call), Cloudflare calls do not currently land
/// in an audit log — the BatchCreateSubdomains handler logs every step
/// inline, and Cloudflare's own audit log on their dashboard is the source of
/// truth for credential / API traceability. A future revision can add a
/// <c>CloudflareOperation</c> table if needed.</para>
/// </summary>
public class CloudflareApiClient
{
    /// <summary>
    /// Cloudflare's API speaks snake_case JSON (<c>tunnel_secret</c>,
    /// <c>account_tag</c>, ...). One shared <see cref="JsonSerializerOptions"/>
    /// — the source-gen pipeline caches reflection metadata per instance, so
    /// reusing the same one is meaningfully cheaper than rebuilding it
    /// per-request.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _httpClient;
    private readonly ICloudflareOptionsAccessor _options;
    private readonly ILogger<CloudflareApiClient> _logger;

    public CloudflareApiClient(
        HttpClient httpClient,
        ICloudflareOptionsAccessor options,
        ILogger<CloudflareApiClient> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
    }

    // ----------------------------------------------------------------------
    // Tunnels
    // ----------------------------------------------------------------------

    /// <summary>
    /// Create a new Cloudflare Tunnel under the configured account. The
    /// returned <see cref="CloudflareTunnel"/> carries both the tunnel id
    /// (used in subsequent ingress / DNS calls) and the connector token —
    /// the long-lived secret <c>cloudflared</c> uses to dial back. The token
    /// must be encrypted by the caller before persisting.
    ///
    /// <para>Endpoint: <c>POST /accounts/{account_id}/cfd_tunnel</c>.
    /// Request body uses <c>config_src=cloudflare</c> so the ingress config
    /// lives on Cloudflare's side (configured via
    /// <see cref="AddPublicHostnameAsync"/>) — this is the modern "remotely
    /// managed" tunnel model.</para>
    /// </summary>
    public async Task<CloudflareTunnel> CreateTunnelAsync(string name, CancellationToken ct = default)
    {
        var accountId = RequireAccountId();
        var payload = new CreateTunnelRequest(name, "cloudflare");
        var request = MakeJsonRequest(HttpMethod.Post, $"accounts/{accountId}/cfd_tunnel", payload);
        return await SendAndUnwrapAsync<CloudflareTunnel>("CreateTunnel", request, ct);
    }

    /// <summary>
    /// Replace the tunnel's ingress configuration so traffic for
    /// <paramref name="hostname"/> is routed to <c>http://localhost:{servicePort}</c>
    /// on whatever machine eventually runs <c>cloudflared</c> with this
    /// tunnel's token. The final entry must be a catch-all <c>http_status:404</c>
    /// — Cloudflare's API rejects ingress configs that don't end with one.
    ///
    /// <para>Endpoint: <c>PUT /accounts/{account_id}/cfd_tunnel/{tunnel_id}/configurations</c>.
    /// PUT is idempotent — re-issuing replaces the entire <c>config</c> blob,
    /// which is exactly the semantics we want for Phase 1 (single ingress per
    /// tunnel, set once at pool creation).</para>
    /// </summary>
    public async Task AddPublicHostnameAsync(
        string tunnelId,
        string hostname,
        int servicePort,
        CancellationToken ct = default)
    {
        var accountId = RequireAccountId();
        var payload = new TunnelConfigurationRequest(
            new TunnelConfigurationBody(new TunnelConfigIngress[]
            {
                new(Hostname: hostname, Service: $"http://localhost:{servicePort}"),
                // Cloudflare requires a catch-all final entry. http_status:404
                // is the canonical "drop unmatched" sentinel.
                new(Hostname: null, Service: "http_status:404"),
            }));

        var request = MakeJsonRequest(
            HttpMethod.Put,
            $"accounts/{accountId}/cfd_tunnel/{tunnelId}/configurations",
            payload);

        // PUT configurations returns the full config blob on success — we
        // don't materialise it; the operation succeeded if the envelope's
        // success flag is true and the status is 2xx.
        await SendAndUnwrapAsync<JsonElement>("PutTunnelConfiguration", request, ct);
    }

    /// <summary>
    /// Create or replace the DNS CNAME pointing
    /// <paramref name="hostname"/> at <c>{tunnelId}.cfargotunnel.com</c> so
    /// public traffic reaches Cloudflare's tunnel edge. The record is
    /// <c>proxied: true</c> — without proxying, the CNAME resolves but
    /// requests bypass the tunnel and 404.
    ///
    /// <para>Endpoint: <c>POST /zones/{zone_id}/dns_records</c>. Phase 1 only
    /// needs create — DNS records for pool subdomains are never updated, only
    /// created on pool fill and torn down with the tunnel on release (Phase 4).</para>
    /// </summary>
    public async Task EnsureDnsRecordAsync(string hostname, string tunnelId, CancellationToken ct = default)
    {
        var zoneId = RequireZoneId();
        var payload = new DnsRecordRequest(
            Type: "CNAME",
            Name: hostname,
            Content: $"{tunnelId}.cfargotunnel.com",
            Ttl: 1,            // 1 == "automatic" in Cloudflare's wire contract
            Proxied: true);

        var request = MakeJsonRequest(HttpMethod.Post, $"zones/{zoneId}/dns_records", payload);
        await SendAndUnwrapAsync<JsonElement>("CreateDnsRecord", request, ct);
    }

    /// <summary>
    /// Tear down a tunnel — used by Phase 4 cleanup and by the batch-create
    /// handler when a partial-failure mid-batch (DNS create failed after
    /// tunnel create succeeded) needs to roll back the orphaned tunnel.
    ///
    /// <para>Endpoint: <c>DELETE /accounts/{account_id}/cfd_tunnel/{tunnel_id}</c>.
    /// Cloudflare returns 200 with a body containing the deleted tunnel; we
    /// don't materialise it.</para>
    /// </summary>
    public async Task DeleteTunnelAsync(string tunnelId, CancellationToken ct = default)
    {
        var accountId = RequireAccountId();
        var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"accounts/{accountId}/cfd_tunnel/{tunnelId}");
        await SendAndUnwrapAsync<JsonElement>("DeleteTunnel", request, ct);
    }

    // ----------------------------------------------------------------------
    // Internals
    // ----------------------------------------------------------------------

    private string RequireAccountId()
    {
        var id = _options.Current.AccountId;
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidOperationException(
                "Cloudflare:AccountId is not configured. Set it in System Settings before " +
                "calling the Cloudflare API.");
        }
        return id;
    }

    private string RequireZoneId()
    {
        var id = _options.Current.ZoneId;
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidOperationException(
                "Cloudflare:ZoneId is not configured. Set it in System Settings before " +
                "calling the Cloudflare API.");
        }
        return id;
    }

    private static HttpRequestMessage MakeJsonRequest<T>(HttpMethod method, string path, T payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        return new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>
    /// Send + parse Cloudflare's envelope. Throws
    /// <see cref="CloudflareApiException"/> for any non-2xx or
    /// <c>success: false</c> response, with the raw body preserved for the
    /// caller's logs. The Bearer token and User-Agent are stamped per-request
    /// so token rotation in SystemSettings doesn't need a process restart.
    /// </summary>
    private async Task<T> SendAndUnwrapAsync<T>(string operation, HttpRequestMessage request, CancellationToken ct)
    {
        var token = _options.Current.ApiToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                "Cloudflare:ApiToken is not configured. Set it in System Settings before " +
                "calling the Cloudflare API.");
        }
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (request.Headers.UserAgent.Count == 0)
        {
            request.Headers.UserAgent.ParseAdd("glenn-platform/1.0");
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cloudflare {Operation} transport failure", operation);
            throw;
        }

        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(ct);
        }
        finally
        {
            // Read body before dispose. The body string is held by the
            // exception (if any) and the unwrap path below.
        }

        _logger.LogInformation(
            "Cloudflare {Operation} {Method} {Path} -> {StatusCode}",
            operation, request.Method, request.RequestUri, (int)response.StatusCode);

        try
        {
            return UnwrapResult<T>(operation, response, body);
        }
        finally
        {
            response.Dispose();
        }
    }

    private static T UnwrapResult<T>(string operation, HttpResponseMessage response, string body)
    {
        // Cloudflare returns the standard { result, success, errors, messages }
        // envelope on both success and most failures. We try to parse it; if
        // the body isn't an envelope (e.g. a 502 from a CF edge proxy returning
        // HTML), we surface the raw status + body in CloudflareApiException so
        // the caller can decide what to do.
        CloudflareEnvelope<T>? env = null;
        try
        {
            env = JsonSerializer.Deserialize<CloudflareEnvelope<T>>(body, JsonOptions);
        }
        catch (JsonException)
        {
            // Fall through to "no envelope" — treated as failure unless 2xx
            // with an empty body (which Cloudflare doesn't do for these
            // endpoints, but we tolerate just in case).
        }

        if (!response.IsSuccessStatusCode || env is { Success: false })
        {
            var errorSummary = env?.Errors is { Count: > 0 }
                ? string.Join("; ", env.Errors.Select(e => $"{e.Code} {e.Message}"))
                : $"HTTP {(int)response.StatusCode}";
            throw new CloudflareApiException(
                (int)response.StatusCode,
                body,
                $"Cloudflare API {operation} failed: {errorSummary}");
        }

        if (env is { Result: { } result })
        {
            return result;
        }

        // 2xx + success: true but no result (rare; some DELETEs do this).
        // Default(T) is fine for JsonElement and the like — the caller only
        // checks for exceptions in that case.
        return default!;
    }
}

// ----------------------------------------------------------------------
// Wire types
// ----------------------------------------------------------------------

/// <summary>Cloudflare's standard response envelope: <c>{ result, success, errors, messages }</c>.</summary>
public record CloudflareEnvelope<T>(
    T? Result,
    bool Success,
    IReadOnlyList<CloudflareError>? Errors,
    IReadOnlyList<CloudflareError>? Messages);

public record CloudflareError(int Code, string Message);

/// <summary>POST /accounts/{account_id}/cfd_tunnel request body.</summary>
public record CreateTunnelRequest(string Name, string ConfigSrc);

/// <summary>POST /accounts/{account_id}/cfd_tunnel response payload (the <c>result</c> field).</summary>
public record CloudflareTunnel(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    /// <summary>
    /// Long-lived connector token. Pass this to <c>cloudflared tunnel run --token</c>
    /// on the machine that should accept traffic for this tunnel. Encrypt
    /// before persisting.
    /// </summary>
    [property: JsonPropertyName("token")] string Token);

/// <summary>PUT /accounts/{account_id}/cfd_tunnel/{id}/configurations request body.</summary>
public record TunnelConfigurationRequest(TunnelConfigurationBody Config);

public record TunnelConfigurationBody(TunnelConfigIngress[] Ingress);

/// <summary>
/// One ingress rule. The catch-all entry passes <c>null</c> for
/// <paramref name="Hostname"/> and <c>http_status:404</c> for
/// <paramref name="Service"/>. <see cref="CloudflareApiClient.JsonOptions"/>
/// is configured to omit null properties so the catch-all serialises as just
/// <c>{ "service": "http_status:404" }</c>.
/// </summary>
public record TunnelConfigIngress(string? Hostname, string Service);

/// <summary>POST /zones/{zone_id}/dns_records request body.</summary>
public record DnsRecordRequest(
    string Type,
    string Name,
    string Content,
    int Ttl,
    bool Proxied);

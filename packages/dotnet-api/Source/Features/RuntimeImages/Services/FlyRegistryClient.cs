using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Source.Features.FlyManagement.Configuration;

namespace Source.Features.RuntimeImages.Services;

/// <summary>
/// Default <see cref="IFlyRegistryClient"/> implementation talking to the Fly registry
/// over the OCI v2 HTTP API (<c>https://registry.fly.io</c>). Auth re-uses the Fly
/// Personal Access Token already stored in <see cref="FlyOptions.ApiToken"/> — the same
/// token <see cref="FlyManagement.FlyClient"/> uses against the Machines API.
///
/// <para><b>Why a named HttpClient and not a typed one.</b> We don't need the resilience
/// pipeline the Machines API client has (the registry calls are read-only, on-demand,
/// human-driven); a plain named client (<c>"FlyRegistry"</c>) keeps wiring lighter and
/// makes mocking in tests a single <c>HttpMessageHandler</c> swap.</para>
///
/// <para><b>Manifest digest source.</b> OCI v2 returns the manifest digest in the
/// <c>Docker-Content-Digest</c> response header. Computing it from the body is also
/// possible but error-prone (whitespace and key-ordering matter for the SHA), so we
/// trust the header and fall back only if it's missing.</para>
/// </summary>
public sealed class FlyRegistryClient : IFlyRegistryClient
{
    public const string HttpClientName = "FlyRegistry";

    /// <summary>
    /// OCI v2 manifest media types we accept. We list both the OCI flavour (the spec)
    /// and the legacy Docker v2 manifest because Fly's registry is happy to serve the
    /// Docker shape for older builds; both have <c>config.digest</c> and <c>config.size</c>
    /// at the top level so we can parse them with the same code path.
    /// </summary>
    private static readonly string[] ManifestMediaTypes =
    {
        "application/vnd.oci.image.manifest.v1+json",
        "application/vnd.docker.distribution.manifest.v2+json",
    };

    /// <summary>
    /// Standard OCI annotation key for the source-control revision (the build's git SHA).
    /// Surfaced via the manifest's config-blob <c>config.Labels</c> dictionary.
    /// </summary>
    public const string GitShaLabelKey = "org.opencontainers.image.revision";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IFlyOptionsAccessor _flyOptions;
    private readonly ILogger<FlyRegistryClient> _logger;

    public FlyRegistryClient(
        IHttpClientFactory httpClientFactory,
        IFlyOptionsAccessor flyOptions,
        ILogger<FlyRegistryClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _flyOptions = flyOptions;
        _logger = logger;
    }

    public async Task<List<string>> ListTagsAsync(string imageName, CancellationToken ct)
    {
        var path = $"/v2/{imageName}/tags/list";
        using var response = await SendAsync(HttpMethod.Get, path, mediaTypes: null, ct);
        await EnsureSuccessAsync(response, $"List tags for image '{imageName}'", ct);

        var json = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("tags", out var tagsElement)
                || tagsElement.ValueKind != JsonValueKind.Array)
            {
                // Some registries omit "tags" entirely when there are none. Treat as empty.
                return new List<string>();
            }

            var tags = new List<string>(tagsElement.GetArrayLength());
            foreach (var t in tagsElement.EnumerateArray())
            {
                if (t.ValueKind == JsonValueKind.String)
                {
                    var s = t.GetString();
                    if (!string.IsNullOrEmpty(s)) tags.Add(s);
                }
            }
            return tags;
        }
        catch (JsonException ex)
        {
            throw new FlyRegistryException(
                FlyRegistryErrorKind.Protocol,
                $"Fly registry returned malformed tags-list JSON for '{imageName}': {ex.Message}",
                ex);
        }
    }

    public async Task<RegistryManifestInfo> GetManifestAsync(string imageName, string tag, CancellationToken ct)
    {
        // 1) Manifest fetch — gives us the manifest digest (header), and the config
        //    blob descriptor we need to follow up on.
        var manifestPath = $"/v2/{imageName}/manifests/{tag}";
        using var manifestResponse = await SendAsync(HttpMethod.Get, manifestPath, ManifestMediaTypes, ct);
        await EnsureSuccessAsync(manifestResponse, $"Get manifest for '{imageName}:{tag}'", ct);

        var manifestDigest = manifestResponse.Headers.TryGetValues("Docker-Content-Digest", out var dcd)
            ? dcd.FirstOrDefault() ?? string.Empty
            : string.Empty;

        var manifestBody = await manifestResponse.Content.ReadAsStringAsync(ct);

        string configDigest;
        long configSize;
        try
        {
            using var doc = JsonDocument.Parse(manifestBody);
            if (!doc.RootElement.TryGetProperty("config", out var config)
                || config.ValueKind != JsonValueKind.Object)
            {
                throw new FlyRegistryException(
                    FlyRegistryErrorKind.Protocol,
                    $"Manifest for '{imageName}:{tag}' has no config object.");
            }

            configDigest = config.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() ?? string.Empty
                : string.Empty;

            configSize = config.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number
                ? s.GetInt64()
                : 0L;
        }
        catch (JsonException ex)
        {
            throw new FlyRegistryException(
                FlyRegistryErrorKind.Protocol,
                $"Manifest for '{imageName}:{tag}' is not valid JSON: {ex.Message}",
                ex);
        }

        if (string.IsNullOrEmpty(configDigest))
        {
            throw new FlyRegistryException(
                FlyRegistryErrorKind.Protocol,
                $"Manifest for '{imageName}:{tag}' is missing config.digest.");
        }

        // Header is the canonical manifest digest; if Fly didn't send it (rare), fall
        // back to the config digest so callers get *something* deterministic.
        if (string.IsNullOrEmpty(manifestDigest))
        {
            manifestDigest = configDigest;
        }

        // 2) Config blob — has the labels (incl. git SHA) and the build timestamp.
        var blobPath = $"/v2/{imageName}/blobs/{configDigest}";
        using var blobResponse = await SendAsync(HttpMethod.Get, blobPath, mediaTypes: null, ct);
        await EnsureSuccessAsync(blobResponse, $"Get config blob for '{imageName}:{tag}'", ct);

        var blobJson = await blobResponse.Content.ReadAsStringAsync(ct);

        DateTime? createdAt = null;
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(blobJson);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("created", out var created)
                && created.ValueKind == JsonValueKind.String
                && DateTime.TryParse(
                    created.GetString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var parsedCreated))
            {
                createdAt = parsedCreated;
            }

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("config", out var innerConfig)
                && innerConfig.ValueKind == JsonValueKind.Object
                && innerConfig.TryGetProperty("Labels", out var labelsElement)
                && labelsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in labelsElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        labels[prop.Name] = prop.Value.GetString() ?? string.Empty;
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            throw new FlyRegistryException(
                FlyRegistryErrorKind.Protocol,
                $"Config blob for '{imageName}:{tag}' is not valid JSON: {ex.Message}",
                ex);
        }

        return new RegistryManifestInfo(
            Digest: manifestDigest,
            SizeBytes: configSize,
            PushedAt: createdAt,
            Labels: labels);
    }

    /// <summary>
    /// Build, dispatch, and return a registry HTTP request. Stamps the bearer token from
    /// <see cref="IFlyOptionsAccessor"/> on every call (the token may have been rotated
    /// in SystemSettings since the last request) and lets the caller specify the
    /// <c>Accept</c> media types when negotiating manifest formats.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string[]? mediaTypes,
        CancellationToken ct)
    {
        var token = _flyOptions.Current.ApiToken;
        if (string.IsNullOrEmpty(token))
        {
            throw new FlyRegistryException(
                FlyRegistryErrorKind.Unauthorized,
                "Fly:ApiToken is not configured — cannot call Fly registry.");
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(method, path);
        var basic = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"x:{token}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        if (mediaTypes is not null)
        {
            foreach (var m in mediaTypes)
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(m));
            }
        }

        try
        {
            return await client.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Fly registry transport failure on {Method} {Path}", method, path);
            throw new FlyRegistryException(
                FlyRegistryErrorKind.Transport,
                $"Fly registry unreachable: {ex.Message}",
                ex);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Fly registry timeout on {Method} {Path}", method, path);
            throw new FlyRegistryException(
                FlyRegistryErrorKind.Transport,
                $"Fly registry request timed out: {ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// Translate a non-2xx response into a typed <see cref="FlyRegistryException"/>.
    /// Reads the body so the caller can include it in the operator-facing error message
    /// without leaking raw <see cref="HttpResponseMessage"/>s up the stack.
    /// </summary>
    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var body = string.Empty;
        try { body = await response.Content.ReadAsStringAsync(ct); } catch { /* best-effort */ }

        var kind = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => FlyRegistryErrorKind.Unauthorized,
            HttpStatusCode.NotFound => FlyRegistryErrorKind.NotFound,
            _ => FlyRegistryErrorKind.Upstream,
        };

        throw new FlyRegistryException(
            kind,
            $"{operation} failed: {(int)response.StatusCode} {response.ReasonPhrase}. Body: {body}");
    }
}

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Source.Features.BoxManagement.Configuration;
using Source.Features.BoxManagement.Models;
using Source.Infrastructure;

namespace Source.Features.BoxManagement;

/// <summary>
/// Thin typed <see cref="HttpClient"/> wrapper for the Box public API
/// (box.ascii.dev — docs at <c>https://docs.ascii.dev/box/api/v1</c>). Every call
/// runs through an audit-aware <c>SendAsync&lt;T&gt;</c> that writes a
/// <see cref="BoxOperation"/> row (Pending pre-flight, Succeeded/Failed once the
/// response lands) so we can trace and idempotently retry — the same pipeline the
/// platform used against Fly, ported wholesale because it earned its keep.
///
/// <para><b>Why a concrete class, not an interface.</b> We follow the existing
/// <see cref="Source.Features.GitHub.Services.GithubApiClient"/> pattern: a thin Accessor
/// is the only abstraction needed for testability — the HTTP transport itself is mocked
/// at the <see cref="HttpMessageHandler"/> seam.</para>
///
/// <para><b>Wire-shape notes.</b> The Box API speaks camelCase JSON with Bearer
/// auth. Response envelopes are tolerated in both bare (<c>{...}</c> / <c>[...]</c>)
/// and wrapped (<c>{"box": ...}</c> / <c>{"boxes": [...]}</c>) forms — see
/// <see cref="UnwrapElement"/>. Anything still uncertain against the live API is
/// pinned by <c>scripts/box-smoke-test.sh</c>, which exercises every verb below
/// against a real account before a deploy is trusted.</para>
/// </summary>
public class BoxClient
{
    /// <summary>
    /// Idempotency replay window. If a Succeeded <see cref="BoxOperation"/> with the same
    /// <c>RequestKey</c> exists within this window we short-circuit the HTTP call and
    /// replay its response body. 60 seconds is the longest realistic gap between a
    /// command-handler retry and the user clicking again — beyond that we want a fresh
    /// call so the upstream actually re-validates the operation.
    /// </summary>
    public static readonly TimeSpan IdempotencyWindow = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Box speaks camelCase JSON. One shared immutable options instance — the JSON
    /// pipeline caches reflection metadata per options instance, so reuse is
    /// meaningfully cheaper than rebuilding per call.
    ///
    /// <para>Note: we deliberately do NOT set <c>DictionaryKeyPolicy</c>. Dictionary keys
    /// in Box request bodies are user-controlled values — environment variable names
    /// (<c>RUNTIME_ID</c>, <c>MAIN_API_URL</c>, ...) — and must pass through verbatim.
    /// Case-converting them silently breaks the daemon's env contract.</para>
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _httpClient;
    private readonly IBoxOptionsAccessor _options;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<BoxClient> _logger;

    public BoxClient(
        HttpClient httpClient,
        IBoxOptionsAccessor options,
        ApplicationDbContext db,
        ILogger<BoxClient> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _db = db;
        _logger = logger;
    }

    // ----------------------------------------------------------------------
    // Account
    // ----------------------------------------------------------------------

    /// <summary>
    /// Lightweight configuration probe. GETs <c>/me</c> with the configured API key.
    /// Returns <c>true</c> only for 200 — unlike an app-namespace lookup there is no
    /// "valid auth but missing resource" case here; any non-200 means the key or the
    /// API is broken. Used by health checks and the admin UI's "test configuration"
    /// button.
    /// </summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.Current.ApiKey))
        {
            _logger.LogWarning("BoxClient.PingAsync called but Box:ApiKey is not configured.");
            return false;
        }

        try
        {
            using var request = BuildRequest(HttpMethod.Get, "me");
            using var response = await SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BoxClient.PingAsync failed.");
            return false;
        }
    }

    // ----------------------------------------------------------------------
    // Box lifecycle
    // ----------------------------------------------------------------------

    /// <summary>
    /// Create a brand-new box from Box's stock Ubuntu base. Only template-building
    /// and admin flows use this — runtime provisioning forks the golden template so
    /// every runtime starts from our prepared filesystem, not a bare VM.
    /// </summary>
    public Task<BoxVm> CreateBoxAsync(
        CreateBoxRequest req,
        string? idempotencyKey = null,
        Guid? runtimeId = null,
        CancellationToken ct = default)
    {
        var payloadJson = JsonSerializer.Serialize(req, JsonOptions);
        var request = BuildRequest(HttpMethod.Post, "boxes", payloadJson);
        return SendBoxAsync("CreateBox", request, runtimeId, idempotencyKey, payloadJson, ct);
    }

    /// <summary>
    /// Fork an existing box into a new one — the platform's provisioning primitive.
    /// The fork inherits the source's entire disk (latest snapshot) and replaces
    /// inherited env with <see cref="ForkBoxRequest.Env"/>; with
    /// <see cref="ForkBoxRequest.NoEnv"/> (always true for runtimes) the fork gets
    /// none of the platform account's own secrets. Forks are usable in seconds at
    /// roughly constant cost regardless of template size.
    /// <paramref name="idempotencyKey"/> protects handler retries from double-forking
    /// (each fork is a billable box AND burns one machine start against the
    /// account-wide 600/hr / 1,500/day budget).
    /// </summary>
    public Task<BoxVm> ForkBoxAsync(
        string sourceBoxId,
        ForkBoxRequest req,
        string? idempotencyKey = null,
        Guid? runtimeId = null,
        CancellationToken ct = default)
    {
        var payloadJson = JsonSerializer.Serialize(req, JsonOptions);
        var request = BuildRequest(HttpMethod.Post, $"boxes/{Uri.EscapeDataString(sourceBoxId)}/fork", payloadJson);
        return SendBoxAsync("ForkBox", request, runtimeId, idempotencyKey, payloadJson, ct);
    }

    /// <summary>Fetch the current state of a single box. Read-only — no idempotency key.</summary>
    public Task<BoxVm> GetBoxAsync(string boxId, CancellationToken ct = default)
    {
        var request = BuildRequest(HttpMethod.Get, $"boxes/{Uri.EscapeDataString(boxId)}");
        return SendBoxAsync("GetBox", request, runtimeId: null, requestKey: null, requestPayloadJson: null, ct);
    }

    /// <summary>List every box on the account. Pagination is not yet exposed.</summary>
    public async Task<List<BoxVm>> ListBoxesAsync(CancellationToken ct = default)
    {
        var request = BuildRequest(HttpMethod.Get, "boxes");
        var body = await SendRawAsync("ListBoxes", request, runtimeId: null, requestKey: null, requestPayloadJson: null, ct);
        return DeserializeList<BoxVm>(body, "boxes");
    }

    /// <summary>
    /// Stop a box: the VM is archived with a fresh disk snapshot and billing pauses.
    /// Files, installed packages and enabled systemd services all survive into the
    /// next resume. This is the platform's suspend primitive.
    /// </summary>
    public Task StopBoxAsync(
        string boxId,
        Guid? runtimeId = null,
        string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        var request = BuildRequest(HttpMethod.Post, $"boxes/{Uri.EscapeDataString(boxId)}/stop", "{}");
        return SendVoidAsync("StopBox", request, runtimeId, idempotencyKey, requestPayloadJson: null, ct);
    }

    /// <summary>
    /// Resume an archived box from its snapshot — the platform's wake primitive.
    /// Counts as one machine start against the account-wide start budget
    /// (600/hr, 1,500/day), which is why wake happens per session, never per message.
    /// </summary>
    public Task ResumeBoxAsync(
        string boxId,
        Guid? runtimeId = null,
        string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        var request = BuildRequest(HttpMethod.Post, $"boxes/{Uri.EscapeDataString(boxId)}/resume", "{}");
        return SendVoidAsync("ResumeBox", request, runtimeId, idempotencyKey, requestPayloadJson: null, ct);
    }

    /// <summary>
    /// Permanently delete a box AND its snapshots — irreversible, so this is wired
    /// only to explicit "delete project" style actions and admin cleanup, never to
    /// idle timeouts (idle = <see cref="StopBoxAsync"/>; the TTL guardrail also
    /// only archives, never deletes). Box requires an explicit confirmation header
    /// on this call as a fat-finger guard.
    /// </summary>
    public Task DeleteBoxAsync(
        string boxId,
        Guid? runtimeId = null,
        CancellationToken ct = default)
    {
        var request = BuildRequest(HttpMethod.Delete, $"boxes/{Uri.EscapeDataString(boxId)}");
        // Confirmation guard required by the API for permanent deletion. Header name
        // pinned by scripts/box-smoke-test.sh against the live API.
        request.Headers.TryAddWithoutValidation("X-Confirm-Delete", boxId);
        return SendVoidAsync("DeleteBox", request, runtimeId, requestKey: null, requestPayloadJson: null, ct);
    }

    /// <summary>
    /// Re-arm a box's TTL. The TTL is the orphan-cost guardrail: a box whose TTL
    /// lapses archives itself (billing stops) even if the control plane lost track
    /// of it. <c>BoxTtlExtenderJob</c> calls this for every runtime we still know
    /// about so healthy boxes never hit the deadline.
    /// </summary>
    public Task<BoxVm> SetTtlAsync(
        string boxId,
        long ttlSeconds,
        Guid? runtimeId = null,
        CancellationToken ct = default)
    {
        var payloadJson = JsonSerializer.Serialize(new UpdateBoxRequest(ttlSeconds), JsonOptions);
        var request = BuildRequest(HttpMethod.Patch, $"boxes/{Uri.EscapeDataString(boxId)}", payloadJson);
        return SendBoxAsync("SetBoxTtl", request, runtimeId, requestKey: null, payloadJson, ct);
    }

    /// <summary>
    /// Run an arbitrary shell command inside a running box (<c>POST /boxes/{id}/commands</c>).
    /// Daemon-independent side channel for admin/debug and the repair loop. Fails with
    /// a retriable <c>box_starting</c> / <c>machine_not_running</c> code (see
    /// <see cref="BoxApiException.IsRetriableStartup"/>) while the box is coming up.
    /// </summary>
    public async Task<RunBoxCommandResponse> RunCommandAsync(
        string boxId,
        string command,
        Guid? runtimeId = null,
        CancellationToken ct = default)
    {
        var payloadJson = JsonSerializer.Serialize(new RunBoxCommandRequest(command), JsonOptions);
        var request = BuildRequest(HttpMethod.Post, $"boxes/{Uri.EscapeDataString(boxId)}/commands", payloadJson);
        var body = await SendRawAsync("RunBoxCommand", request, runtimeId, requestKey: null, payloadJson, ct);
        return DeserializeSingle<RunBoxCommandResponse>(body, "result");
    }

    // ----------------------------------------------------------------------
    // Snapshots
    // ----------------------------------------------------------------------

    /// <summary>List every snapshot on the account (admin cleanup surface).</summary>
    public async Task<List<BoxSnapshot>> ListSnapshotsAsync(CancellationToken ct = default)
    {
        var request = BuildRequest(HttpMethod.Get, "snapshots");
        var body = await SendRawAsync("ListSnapshots", request, runtimeId: null, requestKey: null, requestPayloadJson: null, ct);
        return DeserializeList<BoxSnapshot>(body, "snapshots");
    }

    /// <summary>Permanently delete a snapshot. Irreversible; admin cleanup only.</summary>
    public Task DeleteSnapshotAsync(
        string snapshotId,
        Guid? runtimeId = null,
        CancellationToken ct = default)
    {
        var request = BuildRequest(HttpMethod.Delete, $"snapshots/{Uri.EscapeDataString(snapshotId)}");
        return SendVoidAsync("DeleteSnapshot", request, runtimeId, requestKey: null, requestPayloadJson: null, ct);
    }

    // ----------------------------------------------------------------------
    // Request building
    // ----------------------------------------------------------------------

    /// <summary>
    /// Build a request against the configured API base URL. The base URL comes from
    /// SystemSettings per call (not DI-time) so an operator change never needs a
    /// process restart — same reasoning as per-request auth stamping.
    /// </summary>
    private HttpRequestMessage BuildRequest(HttpMethod method, string path, string? jsonBody = null)
    {
        var baseUrl = _options.Current.ApiBaseUrl.TrimEnd('/');
        var request = new HttpRequestMessage(method, new Uri($"{baseUrl}/{path}"));
        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }
        return request;
    }

    // ----------------------------------------------------------------------
    // Audit-aware send pipeline
    // ----------------------------------------------------------------------

    /// <summary>
    /// Send + deserialize a single-box response, with the full audit/idempotency
    /// pipeline of <see cref="SendRawAsync"/>.
    /// </summary>
    private async Task<BoxVm> SendBoxAsync(
        string operation,
        HttpRequestMessage request,
        Guid? runtimeId,
        string? requestKey,
        string? requestPayloadJson,
        CancellationToken ct)
    {
        var body = await SendRawAsync(operation, request, runtimeId, requestKey, requestPayloadJson, ct);
        return DeserializeSingle<BoxVm>(body, "box");
    }

    /// <summary>
    /// Audit-aware send. Writes a Pending <see cref="BoxOperation"/> row, dispatches the
    /// request, then updates the row to Succeeded/Failed based on the outcome and returns
    /// the raw body. When <paramref name="requestKey"/> matches a recently-Succeeded row
    /// we skip the HTTP call entirely and return the cached response — cheap idempotency
    /// without a Redis or distributed-lock round trip.
    /// </summary>
    private async Task<string> SendRawAsync(
        string operation,
        HttpRequestMessage request,
        Guid? runtimeId,
        string? requestKey,
        string? requestPayloadJson,
        CancellationToken ct)
    {
        // Idempotency replay: only Succeeded rows within the window. Pending or
        // Failed rows must NOT short-circuit — we want the retry to re-attempt.
        if (!string.IsNullOrEmpty(requestKey))
        {
            var since = DateTime.UtcNow - IdempotencyWindow;
            var cached = await _db.BoxOperations
                .Where(o => o.RequestKey == requestKey
                    && o.Status == BoxOperationStatus.Succeeded
                    && o.CreatedAt >= since)
                .OrderByDescending(o => o.CreatedAt)
                .FirstOrDefaultAsync(ct);

            if (cached?.ResponsePayload is { Length: > 0 })
            {
                _logger.LogInformation(
                    "BoxClient idempotency hit on {Operation} key={Key} replaying op {OpId}",
                    operation, requestKey, cached.Id);
                return cached.ResponsePayload;
            }
        }

        var op = new BoxOperation
        {
            Id = Guid.NewGuid(),
            RuntimeId = runtimeId,
            Operation = operation,
            RequestKey = requestKey,
            RequestPayload = string.IsNullOrWhiteSpace(requestPayloadJson) ? "{}" : requestPayloadJson,
            Status = BoxOperationStatus.Pending,
        };
        _db.BoxOperations.Add(op);
        await _db.SaveChangesAsync(ct);

        HttpResponseMessage response;
        string body;
        int latencyMs;
        try
        {
            (response, body, latencyMs) = await TransportAsync(request, ct);
        }
        catch (Exception ex)
        {
            // Transport-level failure: timeout, DNS, connection reset, ... Mark the row
            // Failed with a synthetic error code; HttpStatusCode stays null because we
            // never received one. The exception bubbles untouched so callers can
            // distinguish transport failures from BoxApiException.
            op.Status = BoxOperationStatus.Failed;
            op.ErrorCode = "transport_error";
            await _db.SaveChangesAsync(ct);
            _logger.LogWarning(ex, "BoxClient transport failure on {Operation}", operation);
            throw;
        }

        try
        {
            if (response.IsSuccessStatusCode)
            {
                op.Status = BoxOperationStatus.Succeeded;
                op.HttpStatusCode = (int)response.StatusCode;
                op.LatencyMs = latencyMs;
                op.ResponsePayload = string.IsNullOrWhiteSpace(body) ? null : body;
                await _db.SaveChangesAsync(ct);
                return body;
            }

            var errorCode = TryParseBoxErrorCode(body);
            op.Status = BoxOperationStatus.Failed;
            op.HttpStatusCode = (int)response.StatusCode;
            op.LatencyMs = latencyMs;
            op.ResponsePayload = string.IsNullOrWhiteSpace(body) ? null : body;
            op.ErrorCode = errorCode;
            await _db.SaveChangesAsync(ct);

            throw new BoxApiException(
                (int)response.StatusCode,
                errorCode,
                body,
                $"Box API {operation} failed: {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        finally
        {
            response.Dispose();
        }
    }

    /// <summary>
    /// Variant for endpoints whose body we don't need to materialise (stop, resume,
    /// delete). Still writes the audit row and still throws <see cref="BoxApiException"/>
    /// on non-2xx.
    /// </summary>
    private async Task SendVoidAsync(
        string operation,
        HttpRequestMessage request,
        Guid? runtimeId,
        string? requestKey,
        string? requestPayloadJson,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(requestKey))
        {
            var since = DateTime.UtcNow - IdempotencyWindow;
            var cached = await _db.BoxOperations
                .Where(o => o.RequestKey == requestKey
                    && o.Status == BoxOperationStatus.Succeeded
                    && o.CreatedAt >= since)
                .OrderByDescending(o => o.CreatedAt)
                .FirstOrDefaultAsync(ct);

            if (cached is not null)
            {
                _logger.LogInformation(
                    "BoxClient idempotency hit on {Operation} key={Key} replaying op {OpId}",
                    operation, requestKey, cached.Id);
                return;
            }
        }

        // Replay was already checked above; pass an always-miss key of null? No —
        // the row must still RECORD the key so future retries can replay it. The
        // inner SendRawAsync replay check is harmless (it just misses again).
        await SendRawAsync(operation, request, runtimeId, requestKey, requestPayloadJson, ct);
    }

    /// <summary>
    /// Run the underlying HTTP call and capture the body. Read once and returned as a
    /// string — Box responses are small enough that streaming isn't worth the complexity,
    /// and we need the full string anyway for the audit row's <c>ResponsePayload</c>.
    /// </summary>
    private async Task<(HttpResponseMessage Response, string Body, int LatencyMs)> TransportAsync(
        HttpRequestMessage request,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var response = await SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        stopwatch.Stop();
        return (response, body, (int)stopwatch.ElapsedMilliseconds);
    }

    /// <summary>
    /// Common send pipeline: stamps <c>Authorization</c> and <c>User-Agent</c> headers per
    /// request (intentional — lets the operator rotate the API key via SystemSettings
    /// without a process restart), forwards to the underlying <see cref="HttpClient"/>,
    /// and emits a structured latency log line. The body is not logged here.
    ///
    /// <para>Marked <c>protected internal</c> so unit tests can reach it via
    /// <c>InternalsVisibleTo</c>, while leaving room for a future subclass to override
    /// request shaping if we ever need to.</para>
    /// </summary>
    protected internal async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var key = _options.Current.ApiKey;
        if (!string.IsNullOrEmpty(key))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        if (request.Headers.UserAgent.Count == 0)
        {
            request.Headers.UserAgent.ParseAdd("glenn-platform/1.0");
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await _httpClient.SendAsync(request, ct);
            stopwatch.Stop();

            _logger.LogInformation(
                "Box API {Method} {Path} -> {StatusCode} in {ElapsedMs}ms",
                request.Method,
                request.RequestUri,
                (int)response.StatusCode,
                stopwatch.ElapsedMilliseconds);

            return response;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(
                ex,
                "Box API {Method} {Path} threw after {ElapsedMs}ms",
                request.Method,
                request.RequestUri,
                stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    // ----------------------------------------------------------------------
    // Deserialisation helpers
    // ----------------------------------------------------------------------

    /// <summary>
    /// Deserialise a single resource, tolerating both bare (<c>{...}</c>) and wrapped
    /// (<c>{"box": {...}}</c> / <c>{"data": {...}}</c>) envelope shapes.
    /// </summary>
    private static T DeserializeSingle<T>(string body, string wrapperKey)
    {
        if (string.IsNullOrEmpty(body))
        {
            throw new InvalidOperationException(
                $"Box API returned an empty body where {typeof(T).Name} was expected.");
        }

        using var doc = JsonDocument.Parse(body);
        var element = UnwrapElement(doc.RootElement, wrapperKey);
        var value = element.Deserialize<T>(JsonOptions);
        if (value is null)
        {
            throw new InvalidOperationException(
                $"Box API returned null where {typeof(T).Name} was expected.");
        }
        return value;
    }

    /// <summary>
    /// Deserialise a list, tolerating bare arrays and wrapped
    /// (<c>{"boxes": [...]}</c> / <c>{"items": [...]}</c> / <c>{"data": [...]}</c>) shapes.
    /// </summary>
    private static List<T> DeserializeList<T>(string body, string wrapperKey)
    {
        if (string.IsNullOrEmpty(body))
        {
            return new List<T>();
        }

        using var doc = JsonDocument.Parse(body);
        var element = UnwrapElement(doc.RootElement, wrapperKey);
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"Box API returned a non-array where List<{typeof(T).Name}> was expected.");
        }
        return element.Deserialize<List<T>>(JsonOptions) ?? new List<T>();
    }

    /// <summary>
    /// If <paramref name="root"/> is an object carrying the resource under a known
    /// wrapper key (<paramref name="preferredKey"/>, <c>items</c>, or <c>data</c>),
    /// return that inner element; otherwise return the root untouched.
    /// </summary>
    private static JsonElement UnwrapElement(JsonElement root, string preferredKey)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return root;
        }

        foreach (var key in new[] { preferredKey, "items", "data" })
        {
            if (root.TryGetProperty(key, out var inner)
                && inner.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                return inner;
            }
        }
        return root;
    }

    /// <summary>
    /// Best-effort error-code extractor for Box's non-2xx bodies. Documented shape is
    /// <c>{"error": {"code": "...", "message": "..."}}</c> but we also tolerate flat
    /// <c>{"code": "..."}</c> / <c>{"error": "..."}</c>. Falls back to <c>null</c>
    /// rather than ever throwing — the caller still gets the raw body via
    /// <see cref="BoxApiException.Body"/>.
    /// </summary>
    private static string? TryParseBoxErrorCode(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            string? value = null;
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.String)
                {
                    value = err.GetString();
                }
                else if (err.ValueKind == JsonValueKind.Object
                         && err.TryGetProperty("code", out var innerCode)
                         && innerCode.ValueKind == JsonValueKind.String)
                {
                    value = innerCode.GetString();
                }
            }
            else if (doc.RootElement.TryGetProperty("code", out var code)
                     && code.ValueKind == JsonValueKind.String)
            {
                value = code.GetString();
            }

            // BoxOperations.ErrorCode is varchar(100); error strings can be free-form
            // sentences. Truncate to fit — the full body survives in ResponsePayload.
            if (value is { Length: > 100 })
            {
                value = value.Substring(0, 100);
            }
            return value;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

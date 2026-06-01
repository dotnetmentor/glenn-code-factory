using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Source.Features.FlyManagement.Configuration;
using Source.Features.FlyManagement.Models;
using Source.Infrastructure;

namespace Source.Features.FlyManagement;

/// <summary>
/// Thin typed <see cref="HttpClient"/> wrapper for the Fly.io Machines API
/// (<c>https://api.machines.dev/v1/</c>). Card 2 laid the foundation (auth header,
/// User-Agent, latency logging, <see cref="PingAsync"/>); Card 3 adds Machine CRUD on
/// top of an audit-aware <c>SendAsync&lt;T&gt;</c> that writes a <see cref="FlyOperation"/>
/// row for every call (Pending pre-flight, Succeeded/Failed once the response lands) so
/// we can trace and idempotently retry.
///
/// <para><b>Why a concrete class, not an interface.</b> We follow the existing
/// <see cref="Source.Features.GitHub.Services.GithubApiClient"/> pattern: a thin Accessor
/// is the only abstraction needed for testability — the HTTP transport itself is mocked
/// at the <see cref="HttpMessageHandler"/> seam. Adding <c>IFlyClient</c> would just be
/// indirection for indirection's sake.</para>
/// </summary>
public class FlyClient
{
    /// <summary>
    /// Idempotency replay window. If a Succeeded <see cref="FlyOperation"/> with the same
    /// <c>RequestKey</c> exists within this window we short-circuit the HTTP call and
    /// replay its response body. 60 seconds is the longest realistic gap between a Fly
    /// command-handler retry and the user clicking again — beyond that we want a fresh
    /// call so the upstream actually re-validates the operation.
    /// </summary>
    public static readonly TimeSpan IdempotencyWindow = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Fly's API speaks snake_case JSON (<c>private_ip</c>, <c>internal_port</c>, ...).
    /// One shared <see cref="JsonSerializerOptions"/> instance — these are immutable once
    /// configured and the JSON source-gen pipeline caches reflection metadata per options
    /// instance, so reusing the same one across calls is meaningfully cheaper than
    /// rebuilding it inside each request.
    ///
    /// <para>Note: we deliberately do NOT set <c>DictionaryKeyPolicy</c>. Dictionary keys
    /// in Fly request bodies are user-controlled values — environment variable names
    /// (<c>RUNTIME_ID</c>, <c>MAIN_API_URL</c>), machine metadata labels, etc. — and must
    /// pass through verbatim. Snake-casing them silently breaks the daemon's env contract
    /// (the agent program reads <c>RUNTIME_ID</c>, not <c>runtime_id</c>). Property names
    /// — which are the C# field names we control — are still snake-cased via
    /// <see cref="JsonNamingPolicy.SnakeCaseLower"/>.</para>
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly IFlyOptionsAccessor _options;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<FlyClient> _logger;

    public FlyClient(
        HttpClient httpClient,
        IFlyOptionsAccessor options,
        ApplicationDbContext db,
        ILogger<FlyClient> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Lightweight configuration probe. GETs <c>/apps/{AppName}</c> on the configured Fly
    /// org. Returns <c>true</c> for both 200 (app exists) and 404 (app does not yet exist
    /// but the token is valid and the API reached us). Anything else — auth failures,
    /// network errors, 5xx — yields <c>false</c>. Used by health checks and the admin UI's
    /// "test configuration" button.
    /// </summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        var appName = _options.Current.AppName;
        if (string.IsNullOrWhiteSpace(appName))
        {
            _logger.LogWarning("FlyClient.PingAsync called but Fly:AppName is not configured.");
            return false;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"apps/{appName}");
            using var response = await SendAsync(request, ct);

            // 200 = app reachable; 404 = app missing but auth/transport works — both prove
            // the configuration is wired up. Everything else indicates a real problem.
            return response.StatusCode == HttpStatusCode.OK
                || response.StatusCode == HttpStatusCode.NotFound;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FlyClient.PingAsync failed for app {AppName}.", appName);
            return false;
        }
    }

    // ----------------------------------------------------------------------
    // App operations
    // ----------------------------------------------------------------------

    /// <summary>
    /// Fetch the configured Fly app's metadata. Read-only — no idempotency key. Throws
    /// <see cref="FlyApiException"/> on any non-2xx (including 404 when the app does not
    /// exist; callers that want to tolerate "missing" should catch and inspect
    /// <see cref="FlyApiException.StatusCode"/>, as <see cref="EnsureAppAsync"/> does).
    /// </summary>
    public Task<FlyApp> GetAppAsync(CancellationToken ct = default)
    {
        var appName = _options.Current.AppName;
        var request = new HttpRequestMessage(HttpMethod.Get, $"apps/{appName}");
        return SendAsync<FlyApp>("GetApp", request, runtimeId: null, requestKey: null, requestPayloadJson: null, ct);
    }

    /// <summary>
    /// Idempotent app bootstrap: ensures the configured Fly app exists, creating it on
    /// the user's org if it doesn't. Used by first-boot / bring-up so Card 3+5's machine
    /// and volume operations have a namespace to land under.
    ///
    /// <para><b>Why two separate audited calls and not one "EnsureApp" op.</b> The method
    /// makes either 1 (GET 200) or 2 (GET 404 then POST 200) HTTP round trips, and we
    /// want each one to land its own <see cref="FlyOperation"/> row — collapsing the pair
    /// into a single audit entry would hide whether the app already existed or was
    /// freshly created, exactly the question post-mortems care about. So the GET writes
    /// a "GetApp" row (Failed/404 if missing, Succeeded/200 if present) and the POST
    /// writes a "CreateApp" row when it fires.</para>
    ///
    /// <para>The POST carries an idempotency key (<c>createApp:{appName}</c>) so two
    /// concurrent EnsureApp calls during a race won't both materialise a Succeeded
    /// CreateApp row inside the replay window — the second call short-circuits and
    /// reuses the first's response.</para>
    ///
    /// <para>Any non-404 failure on the initial GET propagates immediately without
    /// attempting the POST: a 401, 403, or 5xx tells us the API isn't healthy, and
    /// trying to create the app on top of that would just produce a second misleading
    /// audit row.</para>
    /// </summary>
    public async Task<FlyApp> EnsureAppAsync(CancellationToken ct = default)
    {
        var appName = _options.Current.AppName;

        try
        {
            return await GetAppAsync(ct);
        }
        catch (FlyApiException ex) when (ex.StatusCode == (int)HttpStatusCode.NotFound)
        {
            // Fall through to create — this is the only status we treat as "absent".
        }

        var orgSlug = _options.Current.OrgSlug;
        var payloadJson = JsonSerializer.Serialize(new CreateAppRequest(appName, orgSlug), JsonOptions);
        var request = new HttpRequestMessage(HttpMethod.Post, "apps")
        {
            Content = new StringContent(payloadJson, Encoding.UTF8, "application/json"),
        };

        // Try to deserialise the POST body directly. Fly's documented contract returns
        // the new app, but a few edge cases historically returned just <c>{"ok":true}</c>;
        // we fall back to a follow-up GET so callers always get a populated FlyApp.
        try
        {
            return await SendAsync<FlyApp>(
                "CreateApp",
                request,
                runtimeId: null,
                requestKey: $"createApp:{appName}",
                requestPayloadJson: payloadJson,
                ct);
        }
        catch (InvalidOperationException)
        {
            // Body wasn't shaped like a FlyApp — issue a fresh GET to materialise it.
            // The CreateApp audit row already landed (Succeeded), so the follow-up GET
            // adds a second "GetApp" row and we still end up with a complete trail.
            return await GetAppAsync(ct);
        }
    }

    // ----------------------------------------------------------------------
    // Machine CRUD
    // ----------------------------------------------------------------------

    /// <summary>
    /// Create a new Fly machine under the configured app. When <paramref name="idempotencyKey"/>
    /// is provided and a Succeeded <see cref="FlyOperation"/> with the same key exists within
    /// <see cref="IdempotencyWindow"/>, the previous machine is returned without a fresh HTTP
    /// call — protects against double-submits across handler retries.
    /// </summary>
    public Task<FlyMachine> CreateMachineAsync(
        CreateMachineRequest req,
        string? idempotencyKey = null,
        Guid? runtimeId = null,
        CancellationToken ct = default)
    {
        var appName = _options.Current.AppName;
        var payloadJson = JsonSerializer.Serialize(req, JsonOptions);
        var request = new HttpRequestMessage(HttpMethod.Post, $"apps/{appName}/machines")
        {
            Content = new StringContent(payloadJson, Encoding.UTF8, "application/json"),
        };
        return SendAsync<FlyMachine>("CreateMachine", request, runtimeId, idempotencyKey, payloadJson, ct);
    }

    /// <summary>Fetch the current state of a single machine. Read-only — no idempotency key.</summary>
    public Task<FlyMachine> GetMachineAsync(string machineId, CancellationToken ct = default)
    {
        var appName = _options.Current.AppName;
        var request = new HttpRequestMessage(HttpMethod.Get, $"apps/{appName}/machines/{machineId}");
        return SendAsync<FlyMachine>("GetMachine", request, runtimeId: null, requestKey: null, requestPayloadJson: null, ct);
    }

    /// <summary>List every machine under the configured app. Pagination is not yet exposed.</summary>
    public Task<List<FlyMachine>> ListMachinesAsync(CancellationToken ct = default)
    {
        var appName = _options.Current.AppName;
        var request = new HttpRequestMessage(HttpMethod.Get, $"apps/{appName}/machines");
        return SendAsync<List<FlyMachine>>("ListMachines", request, runtimeId: null, requestKey: null, requestPayloadJson: null, ct);
    }

    /// <summary>
    /// Destroy a machine. <paramref name="force"/> instructs Fly to skip the graceful stop
    /// and tear down a stuck VM; we still record an audit row either way. Returns no body —
    /// Fly answers with a thin <c>{"ok":true}</c> envelope we don't need to materialise.
    /// </summary>
    public Task DestroyMachineAsync(
        string machineId,
        bool force = false,
        Guid? runtimeId = null,
        CancellationToken ct = default)
    {
        var appName = _options.Current.AppName;
        var path = force
            ? $"apps/{appName}/machines/{machineId}?force=true"
            : $"apps/{appName}/machines/{machineId}";
        var request = new HttpRequestMessage(HttpMethod.Delete, path);
        return SendVoidAsync("DestroyMachine", request, runtimeId, requestKey: null, requestPayloadJson: null, ct);
    }

    // ----------------------------------------------------------------------
    // Machine state transitions
    // ----------------------------------------------------------------------

    /// <summary>
    /// Transition a stopped or suspended machine back to <c>started</c>. Fly returns a
    /// thin <c>{"ok":true}</c> envelope we don't materialise — callers that need to know
    /// when the machine is actually running should follow up with
    /// <see cref="WaitForStateAsync"/>. <paramref name="idempotencyKey"/> protects against
    /// duplicate clicks during a handler retry.
    /// </summary>
    public Task StartMachineAsync(
        string machineId,
        Guid? runtimeId = null,
        string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        var appName = _options.Current.AppName;
        var request = new HttpRequestMessage(HttpMethod.Post, $"apps/{appName}/machines/{machineId}/start");
        return SendVoidAsync("StartMachine", request, runtimeId, idempotencyKey, requestPayloadJson: null, ct);
    }

    /// <summary>
    /// Stop a running machine. With <paramref name="options"/> = <c>null</c> we send an
    /// empty body and let Fly pick its defaults; otherwise the request carries a snake_case
    /// JSON body with the chosen signal and grace period. As with <see cref="StartMachineAsync"/>
    /// the call returns immediately — use <see cref="WaitForStateAsync"/> to observe the
    /// transition complete.
    /// </summary>
    public Task StopMachineAsync(
        string machineId,
        StopMachineRequest? options = null,
        Guid? runtimeId = null,
        string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        var appName = _options.Current.AppName;
        var request = new HttpRequestMessage(HttpMethod.Post, $"apps/{appName}/machines/{machineId}/stop");

        string? payloadJson = null;
        if (options is not null)
        {
            payloadJson = JsonSerializer.Serialize(options, JsonOptions);
            request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
        }
        else
        {
            // Empty JSON body — Fly accepts an absent body but a few of their proxies are
            // happier with an explicit <c>{}</c>, and it costs us nothing.
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        return SendVoidAsync("StopMachine", request, runtimeId, idempotencyKey, payloadJson, ct);
    }

    /// <summary>
    /// Suspend a machine — like Stop but preserves the in-memory state so the next
    /// <see cref="StartMachineAsync"/> resumes faster. Only available on machines that
    /// opted in at config time; Fly returns a 422 otherwise and we surface it as
    /// <see cref="FlyApiException"/>.
    /// </summary>
    public Task SuspendMachineAsync(
        string machineId,
        Guid? runtimeId = null,
        string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        var appName = _options.Current.AppName;
        var request = new HttpRequestMessage(HttpMethod.Post, $"apps/{appName}/machines/{machineId}/suspend");
        return SendVoidAsync("SuspendMachine", request, runtimeId, idempotencyKey, requestPayloadJson: null, ct);
    }

    /// <summary>
    /// Block until the machine reaches <paramref name="targetState"/> or the supplied
    /// <paramref name="timeout"/> expires. Implemented via Fly's native long-poll endpoint
    /// (<c>GET .../wait?state=&amp;timeout=</c>) so we don't have to spin a polling loop
    /// on our side.
    ///
    /// <para>Caveat: the shared <see cref="HttpClient"/> default timeout is 30 seconds
    /// (set in Card 2's DI registration), shorter than the wait we want to permit. We
    /// override it for this call only by linking a <see cref="CancellationTokenSource"/>
    /// configured to cancel a few seconds after Fly's own deadline — that way we trust
    /// Fly to time out first (returning a real 408/504 we can audit) and only kick in if
    /// they hang. <see cref="HttpClient"/> per-request timeouts via
    /// <c>SendAsync(_, ct)</c> only respect the linked CTS, not the global timeout, so
    /// this is the right knob.</para>
    ///
    /// <para>Deliberately not idempotency-keyed: a second wait call is a fresh observation
    /// and must always make the round trip.</para>
    /// </summary>
    public Task<FlyMachineState> WaitForStateAsync(
        string machineId,
        string targetState,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var appName = _options.Current.AppName;
        var seconds = Math.Max(1, (int)timeout.TotalSeconds);
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"apps/{appName}/machines/{machineId}/wait?state={Uri.EscapeDataString(targetState)}&timeout={seconds}");

        // Give Fly a 5-second grace period beyond their own timeout before we cut the cord.
        // Linked so the caller's `ct` still cancels promptly.
        var slack = timeout + TimeSpan.FromSeconds(5);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(slack);

        return SendAsync<FlyMachineState>(
            "WaitForMachineState",
            request,
            runtimeId: null,
            requestKey: null,
            requestPayloadJson: null,
            cts.Token);
    }

    // ----------------------------------------------------------------------
    // Volume CRUD
    // ----------------------------------------------------------------------

    /// <summary>
    /// Create a new persistent volume under the configured app. <see cref="CreateVolumeRequest.Encrypted"/>
    /// defaults to <c>true</c> — see the request record's docs for the rationale; the
    /// <c>CreateVolumeAsync_AlwaysSendsEncryptedTrue</c> security regression test pins it.
    /// <paramref name="idempotencyKey"/> mirrors <see cref="CreateMachineAsync"/>: a
    /// matching Succeeded row inside <see cref="IdempotencyWindow"/> short-circuits the
    /// HTTP call and returns the cached volume, protecting handler retries from creating
    /// duplicate (and billable) storage.
    /// </summary>
    public Task<FlyVolume> CreateVolumeAsync(
        CreateVolumeRequest req,
        string? idempotencyKey = null,
        Guid? runtimeId = null,
        CancellationToken ct = default)
    {
        var appName = _options.Current.AppName;
        var payloadJson = JsonSerializer.Serialize(req, JsonOptions);
        var request = new HttpRequestMessage(HttpMethod.Post, $"apps/{appName}/volumes")
        {
            Content = new StringContent(payloadJson, Encoding.UTF8, "application/json"),
        };
        return SendAsync<FlyVolume>("CreateVolume", request, runtimeId, idempotencyKey, payloadJson, ct);
    }

    /// <summary>
    /// Fork an existing volume into a new one — Fly clones the contents block-for-block on
    /// their side while the source stays live and writable (lab-measured ~15-25s for a 1GB
    /// volume with active postgres writes, zero downtime on the source). Mirrors
    /// <see cref="CreateVolumeAsync"/> exactly: same <c>POST /apps/{app}/volumes</c> endpoint,
    /// same audit row pattern, same <see cref="IdempotencyWindow"/> replay semantics — the
    /// only difference is the request body carries <c>source_volume_id</c>, which flips Fly
    /// from "provision empty" to "fork from remote volume". <paramref name="requireUniqueZone"/>
    /// defaults to <c>true</c> so the clone lands on a different host from the source and a
    /// single hardware fault can't take both down.
    ///
    /// <para><b>No <c>sizeGb</c> parameter.</b> Fly's API explicitly rejects <c>size_gb</c>
    /// on volume forks (HTTP 400, <c>"setting size_gb for volume forks is not currently
    /// supported"</c>) even when the value matches the source — the fork inherits the
    /// source volume's size automatically. We pass <c>SizeGb: null</c> on the DTO so the
    /// <c>WhenWritingNull</c> ignore-condition strips the field from the wire entirely.</para>
    /// </summary>
    public Task<FlyVolume> ForkVolumeAsync(
        string sourceVolumeId,
        string name,
        string region,
        bool requireUniqueZone = true,
        string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        var appName = _options.Current.AppName;
        var req = new CreateVolumeRequest(
            Name: name,
            Region: region,
            // Null → omitted from the JSON via WhenWritingNull. Fly forbids size_gb on
            // forks; the new volume inherits the source's size automatically.
            SizeGb: null,
            // Encrypted intentionally left at its default true — the source volume is
            // already encrypted and we never want a fork to silently weaken that.
            Encrypted: true,
            SourceVolumeId: sourceVolumeId,
            RequireUniqueZone: requireUniqueZone);
        var payloadJson = JsonSerializer.Serialize(req, JsonOptions);
        var request = new HttpRequestMessage(HttpMethod.Post, $"apps/{appName}/volumes")
        {
            Content = new StringContent(payloadJson, Encoding.UTF8, "application/json"),
        };
        // Distinct operation name ("ForkVolume" vs "CreateVolume") so the audit trail
        // distinguishes the two — both hit the same endpoint but the failure modes and
        // expected latency profile are different.
        return SendAsync<FlyVolume>("ForkVolume", request, runtimeId: null, idempotencyKey, payloadJson, ct);
    }

    /// <summary>Fetch the current state of a single volume. Read-only — no idempotency key.</summary>
    public Task<FlyVolume> GetVolumeAsync(string volumeId, CancellationToken ct = default)
    {
        var appName = _options.Current.AppName;
        var request = new HttpRequestMessage(HttpMethod.Get, $"apps/{appName}/volumes/{volumeId}");
        return SendAsync<FlyVolume>("GetVolume", request, runtimeId: null, requestKey: null, requestPayloadJson: null, ct);
    }

    /// <summary>List every volume under the configured app. Pagination is not yet exposed.</summary>
    public Task<List<FlyVolume>> ListVolumesAsync(CancellationToken ct = default)
    {
        var appName = _options.Current.AppName;
        var request = new HttpRequestMessage(HttpMethod.Get, $"apps/{appName}/volumes");
        return SendAsync<List<FlyVolume>>("ListVolumes", request, runtimeId: null, requestKey: null, requestPayloadJson: null, ct);
    }

    /// <summary>
    /// Destroy a volume. Returns no body — Fly answers with a thin <c>{"ok":true}</c>
    /// envelope we don't need to materialise. Audit row is still written either way so
    /// post-mortems can trace which runtime tore down which volume.
    /// </summary>
    public Task DestroyVolumeAsync(
        string volumeId,
        Guid? runtimeId = null,
        CancellationToken ct = default)
    {
        var appName = _options.Current.AppName;
        var request = new HttpRequestMessage(HttpMethod.Delete, $"apps/{appName}/volumes/{volumeId}");
        return SendVoidAsync("DestroyVolume", request, runtimeId, requestKey: null, requestPayloadJson: null, ct);
    }

    /// <summary>
    /// Grow a volume to <paramref name="newSizeGb"/>. Fly rejects shrinks on their side,
    /// so we don't double-validate here. The fresh <see cref="FlyVolume"/> Fly returns
    /// reflects the new size — callers can use it directly without a follow-up GET.
    /// </summary>
    public Task<FlyVolume> ExtendVolumeAsync(
        string volumeId,
        int newSizeGb,
        Guid? runtimeId = null,
        CancellationToken ct = default)
    {
        var appName = _options.Current.AppName;
        var payloadJson = JsonSerializer.Serialize(new ExtendVolumeRequest(newSizeGb), JsonOptions);
        var request = new HttpRequestMessage(HttpMethod.Put, $"apps/{appName}/volumes/{volumeId}/extend")
        {
            Content = new StringContent(payloadJson, Encoding.UTF8, "application/json"),
        };
        return SendAsync<FlyVolume>("ExtendVolume", request, runtimeId, requestKey: null, payloadJson, ct);
    }

    // ----------------------------------------------------------------------
    // Audit-aware send pipeline
    // ----------------------------------------------------------------------

    /// <summary>
    /// Audit-aware send. Writes a Pending <see cref="FlyOperation"/> row, dispatches the
    /// request, then updates the row to Succeeded/Failed based on the outcome — and on
    /// success deserialises the body into <typeparamref name="TResponse"/>. When
    /// <paramref name="requestKey"/> matches a recently-Succeeded row we skip the HTTP
    /// call entirely and return the cached response, giving us cheap idempotency without
    /// a Redis or distributed-lock round trip.
    /// </summary>
    private async Task<TResponse> SendAsync<TResponse>(
        string operation,
        HttpRequestMessage request,
        Guid? runtimeId,
        string? requestKey,
        string? requestPayloadJson,
        CancellationToken ct)
    {
        // Idempotency replay: only consider Succeeded rows within the window. Pending or
        // Failed rows must NOT short-circuit — we want the retry to actually re-attempt.
        if (!string.IsNullOrEmpty(requestKey))
        {
            var since = DateTime.UtcNow - IdempotencyWindow;
            var cached = await _db.FlyOperations
                .Where(o => o.RequestKey == requestKey
                    && o.Status == FlyOperationStatus.Succeeded
                    && o.CreatedAt >= since)
                .OrderByDescending(o => o.CreatedAt)
                .FirstOrDefaultAsync(ct);

            if (cached?.ResponsePayload is { Length: > 0 })
            {
                _logger.LogInformation(
                    "FlyClient idempotency hit on {Operation} key={Key} replaying op {OpId}",
                    operation, requestKey, cached.Id);
                var replayed = Deserialize<TResponse>(cached.ResponsePayload);
                return replayed;
            }
        }

        var op = new FlyOperation
        {
            Id = Guid.NewGuid(),
            RuntimeId = runtimeId,
            Operation = operation,
            RequestKey = requestKey,
            RequestPayload = string.IsNullOrWhiteSpace(requestPayloadJson) ? "{}" : requestPayloadJson,
            Status = FlyOperationStatus.Pending,
        };
        _db.FlyOperations.Add(op);
        await _db.SaveChangesAsync(ct);

        HttpResponseMessage response;
        string body;
        int latencyMs;
        string? requestId;
        try
        {
            (response, body, latencyMs, requestId) = await TransportAsync(request, ct);
        }
        catch (Exception ex)
        {
            // Transport-level failure: timeout, DNS, connection reset, ... Mark the row
            // Failed with a synthetic error code; HttpStatusCode stays null because we
            // never received one. The exception bubbles to the caller untouched so they
            // can distinguish transport failures from FlyApiException.
            op.Status = FlyOperationStatus.Failed;
            op.ErrorCode = "transport_error";
            await _db.SaveChangesAsync(ct);
            _logger.LogWarning(ex, "FlyClient transport failure on {Operation}", operation);
            throw;
        }

        try
        {
            if (response.IsSuccessStatusCode)
            {
                op.Status = FlyOperationStatus.Succeeded;
                op.HttpStatusCode = (int)response.StatusCode;
                op.LatencyMs = latencyMs;
                op.ResponsePayload = string.IsNullOrWhiteSpace(body) ? null : body;
                await _db.SaveChangesAsync(ct);

                return Deserialize<TResponse>(body);
            }

            var errorCode = TryParseFlyErrorCode(body);
            op.Status = FlyOperationStatus.Failed;
            op.HttpStatusCode = (int)response.StatusCode;
            op.LatencyMs = latencyMs;
            op.ResponsePayload = string.IsNullOrWhiteSpace(body) ? null : body;
            op.ErrorCode = errorCode;
            await _db.SaveChangesAsync(ct);

            throw new FlyApiException(
                (int)response.StatusCode,
                errorCode,
                requestId,
                body,
                $"Fly API {operation} failed: {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        finally
        {
            response.Dispose();
        }
    }

    /// <summary>
    /// Variant of <see cref="SendAsync{TResponse}"/> for endpoints whose body we don't
    /// need to materialise (e.g. <c>DELETE</c>, <c>POST /start</c>). Still writes the audit
    /// row and still throws <see cref="FlyApiException"/> on non-2xx.
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
            var cached = await _db.FlyOperations
                .Where(o => o.RequestKey == requestKey
                    && o.Status == FlyOperationStatus.Succeeded
                    && o.CreatedAt >= since)
                .OrderByDescending(o => o.CreatedAt)
                .FirstOrDefaultAsync(ct);

            if (cached is not null)
            {
                _logger.LogInformation(
                    "FlyClient idempotency hit on {Operation} key={Key} replaying op {OpId}",
                    operation, requestKey, cached.Id);
                return;
            }
        }

        var op = new FlyOperation
        {
            Id = Guid.NewGuid(),
            RuntimeId = runtimeId,
            Operation = operation,
            RequestKey = requestKey,
            RequestPayload = string.IsNullOrWhiteSpace(requestPayloadJson) ? "{}" : requestPayloadJson,
            Status = FlyOperationStatus.Pending,
        };
        _db.FlyOperations.Add(op);
        await _db.SaveChangesAsync(ct);

        HttpResponseMessage response;
        string body;
        int latencyMs;
        string? requestId;
        try
        {
            (response, body, latencyMs, requestId) = await TransportAsync(request, ct);
        }
        catch (Exception ex)
        {
            op.Status = FlyOperationStatus.Failed;
            op.ErrorCode = "transport_error";
            await _db.SaveChangesAsync(ct);
            _logger.LogWarning(ex, "FlyClient transport failure on {Operation}", operation);
            throw;
        }

        try
        {
            if (response.IsSuccessStatusCode)
            {
                op.Status = FlyOperationStatus.Succeeded;
                op.HttpStatusCode = (int)response.StatusCode;
                op.LatencyMs = latencyMs;
                op.ResponsePayload = string.IsNullOrWhiteSpace(body) ? null : body;
                await _db.SaveChangesAsync(ct);
                return;
            }

            var errorCode = TryParseFlyErrorCode(body);
            op.Status = FlyOperationStatus.Failed;
            op.HttpStatusCode = (int)response.StatusCode;
            op.LatencyMs = latencyMs;
            op.ResponsePayload = string.IsNullOrWhiteSpace(body) ? null : body;
            op.ErrorCode = errorCode;
            await _db.SaveChangesAsync(ct);

            throw new FlyApiException(
                (int)response.StatusCode,
                errorCode,
                requestId,
                body,
                $"Fly API {operation} failed: {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        finally
        {
            response.Dispose();
        }
    }

    /// <summary>
    /// Run the underlying HTTP call and capture body + headers we care about. Pulled out
    /// of <see cref="SendAsync{TResponse}"/> / <see cref="SendVoidAsync"/> so both share
    /// exactly the same transport behaviour. The body is read once and returned as a
    /// string — Fly responses are small enough that streaming isn't worth the complexity,
    /// and we need the full string anyway for the audit row's <c>ResponsePayload</c>.
    ///
    /// <para>Latency includes the body read, since for callers that's the wall-clock cost
    /// of the operation — the inner <see cref="SendAsync"/> only times the headers.</para>
    /// </summary>
    private async Task<(HttpResponseMessage Response, string Body, int LatencyMs, string? RequestId)> TransportAsync(
        HttpRequestMessage request,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var response = await SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        stopwatch.Stop();

        var requestId = response.Headers.TryGetValues("fly-request-id", out var values)
            ? values.FirstOrDefault()
            : null;

        return (response, body, (int)stopwatch.ElapsedMilliseconds, requestId);
    }

    /// <summary>
    /// Common send pipeline: stamps <c>Authorization</c> and <c>User-Agent</c> headers per
    /// request (intentional — lets the operator rotate the PAT via SystemSettings without a
    /// process restart), forwards to the underlying <see cref="HttpClient"/>, and emits a
    /// structured latency log line. The body is not logged here — Fly responses can include
    /// machine metadata that callers may consider sensitive.
    ///
    /// <para>Marked <c>protected internal</c> so unit tests in the same assembly group can
    /// reach it via <c>InternalsVisibleTo</c>, while leaving room for a future subclass to
    /// override request shaping if we ever need to.</para>
    /// </summary>
    protected internal async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var token = _options.Current.ApiToken;
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        // Idempotent — ParseAdd refuses duplicates by throwing, so we set explicitly only if missing.
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
                "Fly API {Method} {Path} -> {StatusCode} in {ElapsedMs}ms",
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
                "Fly API {Method} {Path} threw after {ElapsedMs}ms",
                request.Method,
                request.RequestUri,
                stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    /// <summary>
    /// Deserialise a Fly response body into the requested shape using the shared
    /// snake_case options. <c>List&lt;T&gt;</c> responses route through the same call —
    /// <see cref="JsonSerializer"/> handles arrays natively. A null deserialised value
    /// (e.g. body was literally <c>"null"</c>) is treated as an upstream protocol error.
    /// </summary>
    private static TResponse Deserialize<TResponse>(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            throw new InvalidOperationException(
                $"Fly API returned an empty body where {typeof(TResponse).Name} was expected.");
        }

        var value = JsonSerializer.Deserialize<TResponse>(body, JsonOptions);
        if (value is null)
        {
            throw new InvalidOperationException(
                $"Fly API returned null where {typeof(TResponse).Name} was expected.");
        }
        return value;
    }

    /// <summary>
    /// Best-effort error-code extractor for Fly's non-2xx bodies. Fly sometimes ships
    /// <c>{"error":"..."}</c>, sometimes <c>{"error":"...","details":"..."}</c>, and
    /// occasionally a free-form HTML page (e.g. behind their edge during incidents).
    /// We try JSON first and fall back to <c>null</c> rather than ever throwing here —
    /// the caller still gets the raw body via <see cref="FlyApiException.Body"/>.
    /// </summary>
    private static string? TryParseFlyErrorCode(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            string? value = null;
            if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
            {
                value = err.GetString();
            }
            else if (doc.RootElement.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String)
            {
                value = code.GetString();
            }
            // FlyOperations.ErrorCode is varchar(100). Fly's "error" field is sometimes
            // a free-form sentence ("could not reserve resource for machine: insufficient
            // memory available to fulfill request"), not a stable code. Truncate to fit
            // the column — the full body is still preserved verbatim in ResponsePayload.
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

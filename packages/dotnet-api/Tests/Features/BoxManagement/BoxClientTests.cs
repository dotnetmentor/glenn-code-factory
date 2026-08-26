using System.Net;
using Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Source.Features.BoxManagement;
using Source.Features.BoxManagement.Configuration;
using Source.Features.BoxManagement.Models;
using Source.Infrastructure;

namespace Api.Tests.Features.BoxManagement;

/// <summary>
/// Wire-shape tests for <see cref="BoxClient"/>: a REAL client on top of a
/// <see cref="ScriptedHandler"/> and an in-memory <see cref="ApplicationDbContext"/>
/// so the audit/idempotency pipeline (BoxOperations rows) is exercised for real.
/// The HttpClient deliberately has NO BaseAddress — BoxClient builds absolute
/// URLs from <see cref="IBoxOptionsAccessor"/> itself. Fake response bodies use
/// the REAL envelopes from the OpenAPI contract (box.info / box.list /
/// box.created / box.error).
/// </summary>
public class BoxClientTests : IDisposable
{
    private const string ApiKey = "box_test_key";
    private const string BaseUrl = "https://ascii.dev/api/box/v1";

    private readonly ApplicationDbContext _db;

    public BoxClientTests()
    {
        _db = TestDbContextFactory.Create();
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private BoxClient CreateClient(ScriptedHandler handler, AuthCapturingHandler? authCapture = null)
    {
        // No BaseAddress on purpose — requests must carry absolute URIs.
        var http = new HttpClient(
            authCapture is null ? handler : authCapture.WithInner(handler),
            disposeHandler: false);
        return new BoxClient(
            http,
            new StubBoxOptionsAccessor(new BoxOptions
            {
                ApiKey = ApiKey,
                ApiBaseUrl = BaseUrl,
            }),
            _db,
            NullLogger<BoxClient>.Instance);
    }

    /// <summary>
    /// Records the Authorization header of every request, then defers to the
    /// inner <see cref="ScriptedHandler"/>. ScriptedHandler itself captures
    /// method/url/body only, so header assertions ride through this wrapper.
    /// </summary>
    private sealed class AuthCapturingHandler : DelegatingHandler
    {
        public string? LastAuthHeader { get; private set; }

        public AuthCapturingHandler WithInner(HttpMessageHandler inner)
        {
            InnerHandler = inner;
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastAuthHeader = request.Headers.Authorization?.ToString();
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>Bare box resource JSON — the payload inside the contract's envelopes.</summary>
    private static string BareBoxJson(string id, string state = "ready") =>
        $$"""
        {"id":"{{id}}","name":"rt-test","state":"{{state}}","type":"small","region":"de","ttlSeconds":21600,"createdAt":"2026-05-08T10:00:00Z"}
        """;

    /// <summary>GET /boxes/{id} envelope: {"ok":true,"type":"box.info","box":{...}}.</summary>
    private static string BoxInfoJson(string id, string state = "ready") =>
        $$"""{"ok":true,"type":"box.info","box":{{BareBoxJson(id, state)}}}""";

    /// <summary>Create/fork envelope: {"type":"box.created","box":{...},"status":"provisioning","ttlSeconds":n}.</summary>
    private static string BoxCreatedJson(string id, string state = "provisioning") =>
        $$"""{"type":"box.created","box":{{BareBoxJson(id, state)}},"status":"provisioning","ttlSeconds":21600}""";

    // ------------------------------------------------------------------
    // 1. Fork wire shape: camelCase properties, VERBATIM env keys, Bearer auth
    // ------------------------------------------------------------------

    [Fact]
    public async Task ForkBoxAsync_SendsCamelCaseBodyWithVerbatimEnvKeysAndBearerAuth()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, BoxCreatedJson("box_fork_1"));

        var authCapture = new AuthCapturingHandler();
        var client = CreateClient(handler, authCapture);

        var result = await client.ForkBoxAsync(
            "box_template_1",
            new ForkBoxRequest(
                Type: "small",
                Env: new Dictionary<string, string>
                {
                    ["RUNTIME_ID"] = "runtime-guid-here",
                    ["MAIN_API_URL"] = "https://api.example.com",
                },
                NoEnv: true,
                TtlSeconds: 21_600));

        result.Id.Should().Be("box_fork_1",
            "the box.created envelope must be unwrapped to the inner box");

        handler.Requests.Should().HaveCount(1);
        var request = handler.Requests[0];
        request.Method.Should().Be(HttpMethod.Post);
        request.Url.Should().Be("https://ascii.dev/api/box/v1/boxes/box_template_1/fork",
            "BoxClient builds ABSOLUTE URLs from the configured ApiBaseUrl");

        // Property names camelCase; the machine tier field is `type` and there
        // is NO `name` field on the fork body per the contract.
        request.Body.Should().Contain("\"type\":\"small\"");
        request.Body.Should().NotContain("\"name\"",
            "the fork body has no name field — naming happens via PATCH afterwards");
        request.Body.Should().Contain("\"noEnv\":true");
        request.Body.Should().Contain("\"ttlSeconds\":21600");
        // ...but dictionary keys pass through VERBATIM — the daemon's env
        // contract reads RUNTIME_ID, never runtime_id.
        request.Body.Should().Contain("\"RUNTIME_ID\"");
        request.Body.Should().NotContain("\"runtime_id\"");

        // Bearer auth stamped per-request from the accessor.
        authCapture.LastAuthHeader.Should().Be($"Bearer {ApiKey}");
    }

    // ------------------------------------------------------------------
    // 2. List deserialisation: the real box.list envelope AND (tolerated) bare arrays
    // ------------------------------------------------------------------

    [Fact]
    public async Task ListBoxesAsync_DeserializesBoxListEnvelope()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK,
            $$$"""{"ok":true,"type":"box.list","boxes":[{{{BareBoxJson("box_a")}}},{{{BareBoxJson("box_b", "archived")}}}],"pageInfo":{"hasNextPage":false}}""");

        var client = CreateClient(handler);
        var boxes = await client.ListBoxesAsync();

        boxes.Should().HaveCount(2);
        boxes[0].Id.Should().Be("box_a");
        boxes[0].State.Should().Be("ready");
        boxes[1].Id.Should().Be("box_b");
        boxes[1].State.Should().Be("archived");

        handler.Requests.Single().Url.Should().Be("https://ascii.dev/api/box/v1/boxes");
    }

    [Fact]
    public async Task ListBoxesAsync_ToleratesBareArray()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, $"[{BareBoxJson("box_bare")}]");

        var client = CreateClient(handler);
        var boxes = await client.ListBoxesAsync();

        boxes.Should().ContainSingle().Which.Id.Should().Be("box_bare");
    }

    // ------------------------------------------------------------------
    // 2b. GET /boxes/{id} unwraps the box.info envelope
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetBoxAsync_UnwrapsBoxInfoEnvelope()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, BoxInfoJson("box_info_1", state: "idle"));

        var client = CreateClient(handler);
        var box = await client.GetBoxAsync("box_info_1");

        box.Id.Should().Be("box_info_1");
        box.State.Should().Be("idle");
        box.Type.Should().Be("small");
    }

    // ------------------------------------------------------------------
    // 3. Non-2xx error parsing → BoxApiException with structured code + requestId
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetBoxAsync_On409BoxStarting_ThrowsRetriableBoxApiException()
    {
        var handler = new ScriptedHandler();
        // Full ErrorEnvelope per the contract — top-level code wins, requestId captured.
        handler.Enqueue(HttpStatusCode.Conflict,
            """{"ok":false,"type":"box.error","status":409,"code":"box_starting","message":"Box is starting","error":{"code":"box_starting","message":"Box is starting","status":409},"requestId":"req_abc123"}""");

        var client = CreateClient(handler);

        var act = () => client.GetBoxAsync("box_slow");

        var ex = (await act.Should().ThrowAsync<BoxApiException>()).Which;
        ex.StatusCode.Should().Be(409);
        ex.ErrorCode.Should().Be("box_starting");
        ex.RequestId.Should().Be("req_abc123",
            "the envelope's requestId must be captured for support correlation");
        ex.IsRetriableStartup.Should().BeTrue(
            "box_starting means 'retry shortly', not 'give up'");
    }

    [Fact]
    public async Task GetBoxAsync_ErrorCodeExtraction_PrefersTopLevelCodeThenErrorCode()
    {
        var handler = new ScriptedHandler();
        // No top-level code — must fall back to error.code.
        handler.Enqueue((HttpStatusCode)429,
            """{"ok":false,"type":"box.error","status":429,"message":"limited","error":{"code":"start_limit_reached","message":"limited","status":429},"requestId":"req_rl_1"}""");

        var client = CreateClient(handler);

        var act = () => client.GetBoxAsync("box_rl");

        var ex = (await act.Should().ThrowAsync<BoxApiException>()).Which;
        ex.StatusCode.Should().Be(429);
        ex.ErrorCode.Should().Be("start_limit_reached");
        ex.IsRateLimited.Should().BeTrue(
            "start_limit_reached on 429 is the contract's machine-start budget signal");
    }

    // ------------------------------------------------------------------
    // 4. Idempotency replay via BoxOperations
    // ------------------------------------------------------------------

    [Fact]
    public async Task CreateBoxAsync_SameIdempotencyKeyTwice_OnlyOneHttpRequest()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, BoxCreatedJson("box_idem"));
        // Deliberately only ONE scripted response — a second HTTP call would
        // throw "exhausted" and fail the test.

        var client = CreateClient(handler);
        var req = new CreateBoxRequest(Type: "small");

        var first = await client.CreateBoxAsync(req, idempotencyKey: "create-box:test-key");
        var second = await client.CreateBoxAsync(req, idempotencyKey: "create-box:test-key");

        handler.CallCount.Should().Be(1,
            "the second call must be served from the BoxOperations replay cache");
        first.Id.Should().Be("box_idem");
        second.Id.Should().Be("box_idem", "the replayed response body deserialises identically");

        // The create body carries `type`, never `name`/`size`.
        handler.Requests.Single().Body.Should().Contain("\"type\":\"small\"");
        handler.Requests.Single().Body.Should().NotContain("\"name\"");

        // Exactly one Succeeded audit row carries the key.
        var ops = await _db.BoxOperations.AsNoTracking()
            .Where(o => o.RequestKey == "create-box:test-key")
            .ToListAsync();
        ops.Should().ContainSingle().Which.Status.Should().Be(BoxOperationStatus.Succeeded);
    }

    // ------------------------------------------------------------------
    // 5. Command endpoint: SINGULAR path + contract request/response fields
    // ------------------------------------------------------------------

    [Fact]
    public async Task RunCommandAsync_UsesSingularCommandPathAndContractShapes()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK,
            """{"exitCode":0,"stdout":"hi\n","stderr":"","timedOut":false,"stdoutTruncated":false,"stderrTruncated":false,"startedAt":"2026-05-08T10:00:00Z","finishedAt":"2026-05-08T10:00:01Z"}""");

        var client = CreateClient(handler);
        var result = await client.RunCommandAsync("box_cmd", "echo hi", timeoutSeconds: 120);

        result.ExitCode.Should().Be(0);
        result.Stdout.Should().Be("hi\n");
        result.TimedOut.Should().BeFalse();

        var request = handler.Requests.Single();
        request.Url.Should().Be("https://ascii.dev/api/box/v1/boxes/box_cmd/command",
            "the contract's command endpoint is SINGULAR — /command, not /commands");
        request.Body.Should().Contain("\"command\":\"echo hi\"");
        request.Body.Should().Contain("\"timeoutSeconds\":120",
            "long-running commands must carry an explicit timeout (contract default is 30s)");
    }

    // ------------------------------------------------------------------
    // 6. Delete: X-Ascii-Confirm-Delete confirmation header
    // ------------------------------------------------------------------

    [Fact]
    public async Task DeleteBoxAsync_SendsAsciiConfirmDeleteHeader()
    {
        var headerCapture = new HeaderCapturingHandler();
        var scripted = new ScriptedHandler();
        scripted.Enqueue(HttpStatusCode.NoContent, "");

        var http = new HttpClient(headerCapture.WithInner(scripted), disposeHandler: false);
        var client = new BoxClient(
            http,
            new StubBoxOptionsAccessor(new BoxOptions { ApiKey = ApiKey, ApiBaseUrl = BaseUrl }),
            _db,
            NullLogger<BoxClient>.Instance);

        await client.DeleteBoxAsync("box_del_1");

        scripted.Requests.Single().Method.Should().Be(HttpMethod.Delete);
        scripted.Requests.Single().Url.Should().Be("https://ascii.dev/api/box/v1/boxes/box_del_1");
        headerCapture.LastConfirmHeader.Should().Be("box_del_1",
            "the contract requires X-Ascii-Confirm-Delete: {boxId} (409 when missing/mismatched)");
    }

    private sealed class HeaderCapturingHandler : DelegatingHandler
    {
        public string? LastConfirmHeader { get; private set; }

        public HeaderCapturingHandler WithInner(HttpMessageHandler inner)
        {
            InnerHandler = inner;
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.TryGetValues("X-Ascii-Confirm-Delete", out var values))
            {
                LastConfirmHeader = values.FirstOrDefault();
            }
            return base.SendAsync(request, cancellationToken);
        }
    }

    // ------------------------------------------------------------------
    // 7. Snapshots are per-box
    // ------------------------------------------------------------------

    [Fact]
    public async Task ListSnapshotsAsync_UsesPerBoxPathAndUnwrapsSnapshots()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK,
            """{"ok":true,"snapshots":[{"id":"snap_1","status":"complete","generation":3,"createdAt":"2026-05-08T10:00:00Z","sizeBytes":1024,"fileCount":42,"contentSizeBytes":900}]}""");

        var client = CreateClient(handler);
        var snapshots = await client.ListSnapshotsAsync("box_snap");

        snapshots.Should().ContainSingle();
        snapshots[0].Id.Should().Be("snap_1");
        snapshots[0].Generation.Should().Be(3);
        snapshots[0].SizeBytes.Should().Be(1024);

        handler.Requests.Single().Url.Should().Be(
            "https://ascii.dev/api/box/v1/boxes/box_snap/snapshots",
            "the contract exposes snapshots per box, not account-level");
    }

    // ------------------------------------------------------------------
    // Bearer auth header (dedicated capture, since ScriptedHandler records
    // method/url/body only)
    // ------------------------------------------------------------------

    [Fact]
    public async Task SendAsync_StampsBearerAuthHeaderPerRequest()
    {
        var scripted = new ScriptedHandler();
        scripted.Enqueue(HttpStatusCode.OK, BoxInfoJson("box_auth"));

        var authCapture = new AuthCapturingHandler();
        var client = CreateClient(scripted, authCapture);

        await client.GetBoxAsync("box_auth");

        authCapture.LastAuthHeader.Should().Be($"Bearer {ApiKey}");
    }
}

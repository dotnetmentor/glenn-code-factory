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
/// URLs from <see cref="IBoxOptionsAccessor"/> itself.
/// </summary>
public class BoxClientTests : IDisposable
{
    private const string ApiKey = "box_test_key";
    private const string BaseUrl = "https://api.ascii.dev/v1";

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

    private static string BoxJson(string id, string status = "ready") =>
        $$"""
        {"id":"{{id}}","name":"rt-test","status":"{{status}}","size":"small","region":"de","ttlSeconds":21600,"createdAt":"2026-05-08T10:00:00Z"}
        """;

    // ------------------------------------------------------------------
    // 1. Fork wire shape: camelCase properties, VERBATIM env keys, Bearer auth
    // ------------------------------------------------------------------

    [Fact]
    public async Task ForkBoxAsync_SendsCamelCaseBodyWithVerbatimEnvKeysAndBearerAuth()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, BoxJson("box_fork_1"));

        var authCapture = new AuthCapturingHandler();
        var client = CreateClient(handler, authCapture);

        var result = await client.ForkBoxAsync(
            "box_template_1",
            new ForkBoxRequest(
                Name: "rt-abc",
                Size: "small",
                Env: new Dictionary<string, string>
                {
                    ["RUNTIME_ID"] = "runtime-guid-here",
                    ["MAIN_API_URL"] = "https://api.example.com",
                },
                NoEnv: true,
                TtlSeconds: 21_600));

        result.Id.Should().Be("box_fork_1");

        handler.Requests.Should().HaveCount(1);
        var request = handler.Requests[0];
        request.Method.Should().Be(HttpMethod.Post);
        request.Url.Should().Be("https://api.ascii.dev/v1/boxes/box_template_1/fork",
            "BoxClient builds ABSOLUTE URLs from the configured ApiBaseUrl");

        // Property names camelCase...
        request.Body.Should().Contain("\"name\":\"rt-abc\"");
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
    // 2. List deserialisation tolerates bare arrays AND {"boxes":[...]} wrappers
    // ------------------------------------------------------------------

    [Fact]
    public async Task ListBoxesAsync_DeserializesBareArray()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, $"[{BoxJson("box_a")},{BoxJson("box_b", "archived")}]");

        var client = CreateClient(handler);
        var boxes = await client.ListBoxesAsync();

        boxes.Should().HaveCount(2);
        boxes[0].Id.Should().Be("box_a");
        boxes[0].Status.Should().Be("ready");
        boxes[1].Id.Should().Be("box_b");
        boxes[1].Status.Should().Be("archived");

        handler.Requests.Single().Url.Should().Be("https://api.ascii.dev/v1/boxes");
    }

    [Fact]
    public async Task ListBoxesAsync_DeserializesWrappedEnvelope()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, $$"""{"boxes":[{{BoxJson("box_wrapped")}}]}""");

        var client = CreateClient(handler);
        var boxes = await client.ListBoxesAsync();

        boxes.Should().ContainSingle().Which.Id.Should().Be("box_wrapped");
    }

    // ------------------------------------------------------------------
    // 3. Non-2xx error parsing → BoxApiException with structured code
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetBoxAsync_On409BoxStarting_ThrowsRetriableBoxApiException()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.Conflict, """{"error":{"code":"box_starting","message":"Box is starting"}}""");

        var client = CreateClient(handler);

        var act = () => client.GetBoxAsync("box_slow");

        var ex = (await act.Should().ThrowAsync<BoxApiException>()).Which;
        ex.StatusCode.Should().Be(409);
        ex.ErrorCode.Should().Be("box_starting");
        ex.IsRetriableStartup.Should().BeTrue(
            "box_starting means 'retry shortly', not 'give up'");
    }

    // ------------------------------------------------------------------
    // 4. Idempotency replay via BoxOperations
    // ------------------------------------------------------------------

    [Fact]
    public async Task CreateBoxAsync_SameIdempotencyKeyTwice_OnlyOneHttpRequest()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, BoxJson("box_idem"));
        // Deliberately only ONE scripted response — a second HTTP call would
        // throw "exhausted" and fail the test.

        var client = CreateClient(handler);
        var req = new CreateBoxRequest(Name: "rt-idem", Size: "small");

        var first = await client.CreateBoxAsync(req, idempotencyKey: "create-box:test-key");
        var second = await client.CreateBoxAsync(req, idempotencyKey: "create-box:test-key");

        handler.CallCount.Should().Be(1,
            "the second call must be served from the BoxOperations replay cache");
        first.Id.Should().Be("box_idem");
        second.Id.Should().Be("box_idem", "the replayed response body deserialises identically");

        // Exactly one Succeeded audit row carries the key.
        var ops = await _db.BoxOperations.AsNoTracking()
            .Where(o => o.RequestKey == "create-box:test-key")
            .ToListAsync();
        ops.Should().ContainSingle().Which.Status.Should().Be(BoxOperationStatus.Succeeded);
    }

    // ------------------------------------------------------------------
    // Bearer auth header (dedicated capture, since ScriptedHandler records
    // method/url/body only)
    // ------------------------------------------------------------------

    [Fact]
    public async Task SendAsync_StampsBearerAuthHeaderPerRequest()
    {
        var scripted = new ScriptedHandler();
        scripted.Enqueue(HttpStatusCode.OK, BoxJson("box_auth"));

        var authCapture = new AuthCapturingHandler();
        var client = CreateClient(scripted, authCapture);

        await client.GetBoxAsync("box_auth");

        authCapture.LastAuthHeader.Should().Be($"Bearer {ApiKey}");
    }
}

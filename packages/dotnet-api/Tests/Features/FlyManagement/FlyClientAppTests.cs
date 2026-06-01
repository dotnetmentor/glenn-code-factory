using System.Net;
using System.Text;
using Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Source.Features.FlyManagement;
using Source.Features.FlyManagement.Configuration;
using Source.Features.FlyManagement.Models;
using Source.Infrastructure;

namespace Api.Tests.Features.FlyManagement;

/// <summary>
/// Card 6 coverage: app-level operations on <see cref="FlyClient"/> — <c>GetAppAsync</c>
/// and the idempotent <c>EnsureAppAsync</c> bootstrap. Mirrors the structure of
/// <see cref="FlyClientMachineTests"/>, <see cref="FlyClientStateTests"/>, and
/// <see cref="FlyClientVolumeTests"/>: assert wire shape, deserialised return, and the
/// audit trail.
///
/// <para>The interesting test in this slice is the audit-row pair check: EnsureApp's
/// contract is "either 1 or 2 HTTP calls, each fully audited", and post-mortems rely on
/// the GetApp/CreateApp pair to tell whether the app was discovered or freshly created.
/// <c>EnsureAppAsync_WritesBothAuditRows</c> pins that contract.</para>
/// </summary>
public class FlyClientAppTests : IDisposable
{
    private static readonly FlyOptions DefaultOptions = new()
    {
        ApiToken = "fly_pat_secret_xyz",
        OrgSlug = "personal",
        AppName = "test-app",
        DefaultRegion = "arn",
    };

    private readonly ApplicationDbContext _db = TestDbContextFactory.Create();

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private (FlyClient client, Mock<HttpMessageHandler> handler) BuildClient(FlyOptions? options = null)
    {
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        var http = new HttpClient(handler.Object, disposeHandler: false)
        {
            BaseAddress = new Uri("https://api.machines.dev/v1/"),
        };
        var client = new FlyClient(
            http,
            new StubFlyOptionsAccessor(options ?? DefaultOptions),
            _db,
            new Mock<ILogger<FlyClient>>().Object);
        return (client, handler);
    }

    /// <summary>
    /// Single-response setup. For tests that exercise EnsureApp's two-call path we use
    /// <see cref="SetupResponseSequence"/> instead so the GET and POST can be matched
    /// to distinct status codes / bodies.
    /// </summary>
    private static void SetupResponse(
        Mock<HttpMessageHandler> handler,
        HttpStatusCode status,
        string body,
        Action<HttpRequestMessage>? capture = null,
        string? flyRequestId = null)
    {
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capture?.Invoke(req))
            .ReturnsAsync(() =>
            {
                var resp = new HttpResponseMessage(status)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                if (flyRequestId is not null)
                {
                    resp.Headers.Add("fly-request-id", flyRequestId);
                }
                return resp;
            });
    }

    /// <summary>
    /// Sequence-based response setup. Each invocation of the mock returns the next entry
    /// in <paramref name="responses"/>; captured requests land in
    /// <paramref name="capturedRequests"/> in call order. We need this for EnsureApp
    /// because its second HTTP call (the POST) only happens when the first (GET)
    /// returned 404 — the two responses must therefore differ.
    /// </summary>
    private static void SetupResponseSequence(
        Mock<HttpMessageHandler> handler,
        List<HttpRequestMessage> capturedRequests,
        params (HttpStatusCode Status, string Body)[] responses)
    {
        var index = 0;
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) =>
            {
                // Snapshot the request because Moq holds the live message which the
                // FlyClient will dispose after the response comes back.
                capturedRequests.Add(req);
            })
            .ReturnsAsync(() =>
            {
                var (status, body) = responses[index++];
                return new HttpResponseMessage(status)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
            });
    }

    private const string SampleAppBody = """
        {
            "id": "app_abc",
            "name": "test-app",
            "org_slug": "personal",
            "status": "deployed",
            "created_at": "2026-05-08T10:00:00Z"
        }
        """;

    // ----------------------------------------------------------------------
    // GetApp
    // ----------------------------------------------------------------------

    [Fact]
    public async Task GetAppAsync_ReturnsApp()
    {
        var (client, handler) = BuildClient();
        HttpRequestMessage? captured = null;
        SetupResponse(handler, HttpStatusCode.OK, SampleAppBody, req => captured = req);

        var app = await client.GetAppAsync();

        // Wire shape — single GET to /apps/{name}.
        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Get);
        captured.RequestUri!.AbsoluteUri.Should().Be("https://api.machines.dev/v1/apps/test-app");

        // Snake-case fields round-trip through the shared serialiser.
        app.Id.Should().Be("app_abc");
        app.Name.Should().Be("test-app");
        app.OrgSlug.Should().Be("personal");
        app.Status.Should().Be("deployed");

        // Audit row records the success.
        var op = await _db.FlyOperations.SingleAsync();
        op.Operation.Should().Be("GetApp");
        op.Status.Should().Be(FlyOperationStatus.Succeeded);
        op.HttpStatusCode.Should().Be(200);
    }

    [Fact]
    public async Task GetAppAsync_404_ThrowsFlyApiException()
    {
        var (client, handler) = BuildClient();
        SetupResponse(
            handler,
            HttpStatusCode.NotFound,
            "{\"error\":\"app not found\"}",
            flyRequestId: "req-app-404");

        var act = async () => await client.GetAppAsync();

        var ex = await act.Should().ThrowAsync<FlyApiException>();
        ex.Which.StatusCode.Should().Be(404);
        ex.Which.ErrorCode.Should().Be("app not found");
        ex.Which.RequestId.Should().Be("req-app-404");

        // The Failed audit row is what EnsureAppAsync_WritesBothAuditRows leans on too.
        var op = await _db.FlyOperations.SingleAsync();
        op.Operation.Should().Be("GetApp");
        op.Status.Should().Be(FlyOperationStatus.Failed);
        op.HttpStatusCode.Should().Be(404);
    }

    // ----------------------------------------------------------------------
    // EnsureApp
    // ----------------------------------------------------------------------

    [Fact]
    public async Task EnsureAppAsync_AppExists_ReturnsWithoutCreating()
    {
        var (client, handler) = BuildClient();
        var captured = new List<HttpRequestMessage>();
        SetupResponseSequence(handler, captured, (HttpStatusCode.OK, SampleAppBody));

        var app = await client.EnsureAppAsync();

        // Existing app — single GET, no POST.
        captured.Should().HaveCount(1);
        captured[0].Method.Should().Be(HttpMethod.Get);
        captured[0].RequestUri!.AbsoluteUri.Should().Be("https://api.machines.dev/v1/apps/test-app");

        app.Id.Should().Be("app_abc");
        app.Name.Should().Be("test-app");

        // Belt-and-braces: zero POSTs ever flew the wire — matched by method.
        handler.Protected().Verify(
            "SendAsync",
            Times.Never(),
            ItExpr.Is<HttpRequestMessage>(m => m.Method == HttpMethod.Post),
            ItExpr.IsAny<CancellationToken>());

        var ops = await _db.FlyOperations.ToListAsync();
        ops.Should().HaveCount(1);
        ops[0].Operation.Should().Be("GetApp");
        ops[0].Status.Should().Be(FlyOperationStatus.Succeeded);
    }

    [Fact]
    public async Task EnsureAppAsync_AppMissing_CreatesAndReturns()
    {
        var (client, handler) = BuildClient();
        var captured = new List<HttpRequestMessage>();
        SetupResponseSequence(
            handler,
            captured,
            (HttpStatusCode.NotFound, "{\"error\":\"app not found\"}"),
            (HttpStatusCode.OK, SampleAppBody));

        var app = await client.EnsureAppAsync();

        // Two HTTP calls: GET /apps/test-app then POST /apps.
        captured.Should().HaveCount(2);

        captured[0].Method.Should().Be(HttpMethod.Get);
        captured[0].RequestUri!.AbsoluteUri.Should().Be("https://api.machines.dev/v1/apps/test-app");

        captured[1].Method.Should().Be(HttpMethod.Post);
        captured[1].RequestUri!.AbsoluteUri.Should().Be("https://api.machines.dev/v1/apps");

        // POST body uses snake_case as required by Fly.
        var sentBody = await captured[1].Content!.ReadAsStringAsync();
        sentBody.Should().Contain("\"app_name\":\"test-app\"");
        sentBody.Should().Contain("\"org_slug\":\"personal\"");

        // Returned app reflects the freshly created resource.
        app.Id.Should().Be("app_abc");
        app.Name.Should().Be("test-app");
    }

    [Fact]
    public async Task EnsureAppAsync_GetFails_NonNotFound_PropagatesException()
    {
        var (client, handler) = BuildClient();
        var captured = new List<HttpRequestMessage>();
        SetupResponseSequence(
            handler,
            captured,
            (HttpStatusCode.InternalServerError, "{\"error\":\"upstream\"}"));

        var act = async () => await client.EnsureAppAsync();

        var ex = await act.Should().ThrowAsync<FlyApiException>();
        ex.Which.StatusCode.Should().Be(500);

        // Exactly one HTTP call — the POST must NOT fire on a non-404 GET failure,
        // otherwise we'd be slamming Fly's create endpoint while their API is degraded.
        captured.Should().HaveCount(1);
        captured[0].Method.Should().Be(HttpMethod.Get);

        // And exactly one audit row (GetApp/Failed) — no CreateApp attempt was made.
        var ops = await _db.FlyOperations.ToListAsync();
        ops.Should().HaveCount(1);
        ops[0].Operation.Should().Be("GetApp");
        ops[0].Status.Should().Be(FlyOperationStatus.Failed);
        ops[0].HttpStatusCode.Should().Be(500);
    }

    /// <summary>
    /// The contract-load-bearing test for this card: when EnsureApp had to create the
    /// app, the audit table must show <i>both</i> the failed GetApp probe (with HTTP 404)
    /// <i>and</i> the succeeded CreateApp. That pair is what lets ops answer "did this
    /// runtime's bootstrap discover an existing app or create a new one?" from the audit
    /// trail alone, without grepping logs.
    /// </summary>
    [Fact]
    public async Task EnsureAppAsync_WritesBothAuditRows()
    {
        var (client, handler) = BuildClient();
        var captured = new List<HttpRequestMessage>();
        SetupResponseSequence(
            handler,
            captured,
            (HttpStatusCode.NotFound, "{\"error\":\"app not found\"}"),
            (HttpStatusCode.OK, SampleAppBody));

        await client.EnsureAppAsync();

        var ops = await _db.FlyOperations
            .OrderBy(o => o.CreatedAt)
            .ThenBy(o => o.Id)
            .ToListAsync();
        ops.Should().HaveCount(2);

        // First row: the probe that came back 404.
        ops[0].Operation.Should().Be("GetApp");
        ops[0].Status.Should().Be(FlyOperationStatus.Failed);
        ops[0].HttpStatusCode.Should().Be(404);

        // Second row: the create that succeeded — and carries the idempotency key
        // so a parallel EnsureApp would short-circuit instead of double-creating.
        ops[1].Operation.Should().Be("CreateApp");
        ops[1].Status.Should().Be(FlyOperationStatus.Succeeded);
        ops[1].HttpStatusCode.Should().Be(200);
        ops[1].RequestKey.Should().Be("createApp:test-app");
        ops[1].RequestPayload.Should().Contain("\"app_name\":\"test-app\"");
        ops[1].RequestPayload.Should().Contain("\"org_slug\":\"personal\"");
    }
}

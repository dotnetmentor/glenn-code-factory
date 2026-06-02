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
/// Card 4 coverage: Machine state transitions on <see cref="FlyClient"/> — start, stop,
/// suspend, and the long-polling wait. Mirrors the structure of
/// <see cref="FlyClientMachineTests"/>: assert the wire shape of every request, that the
/// audit row lands in the expected status, and that error mapping carries through.
///
/// <para>Kept as a separate file (rather than appended to the Machine tests) so each
/// vertical of the FlyClient stays small enough to read top-to-bottom — Card 3 already
/// carries the CRUD verbs and growing it further blurs the seam.</para>
/// </summary>
public class FlyClientStateTests : IDisposable
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

    // ----------------------------------------------------------------------
    // StartMachine
    // ----------------------------------------------------------------------

    [Fact]
    public async Task StartMachineAsync_posts_to_correct_url_and_writes_succeeded_row()
    {
        var (client, handler) = BuildClient();
        HttpRequestMessage? captured = null;
        SetupResponse(handler, HttpStatusCode.OK, "{\"ok\":true}", req => captured = req);

        var runtimeId = Guid.NewGuid();
        await client.StartMachineAsync("m-1", runtimeId: runtimeId);

        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri!.AbsoluteUri
            .Should().Be("https://api.machines.dev/v1/apps/test-app/machines/m-1/start");

        var op = await _db.FlyOperations.SingleAsync();
        op.Operation.Should().Be("StartMachine");
        op.Status.Should().Be(FlyOperationStatus.Succeeded);
        op.HttpStatusCode.Should().Be(200);
        op.RuntimeId.Should().Be(runtimeId);
    }

    // ----------------------------------------------------------------------
    // StopMachine
    // ----------------------------------------------------------------------

    [Fact]
    public async Task StopMachineAsync_with_signal_sends_body_with_signal_and_timeout()
    {
        var (client, handler) = BuildClient();
        HttpRequestMessage? captured = null;
        SetupResponse(handler, HttpStatusCode.OK, "{\"ok\":true}", req => captured = req);

        var options = new StopMachineRequest(Signal: "SIGTERM", Timeout: 30);
        await client.StopMachineAsync("m-2", options, runtimeId: Guid.NewGuid());

        captured!.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri!.AbsoluteUri
            .Should().Be("https://api.machines.dev/v1/apps/test-app/machines/m-2/stop");

        var sentBody = await captured.Content!.ReadAsStringAsync();
        sentBody.Should().Contain("\"signal\":\"SIGTERM\"");
        sentBody.Should().Contain("\"timeout\":30");

        var op = await _db.FlyOperations.SingleAsync();
        op.Operation.Should().Be("StopMachine");
        op.Status.Should().Be(FlyOperationStatus.Succeeded);
        // The audit row carries the same body we put on the wire — important for replay.
        op.RequestPayload.Should().Contain("\"signal\":\"SIGTERM\"");
        op.RequestPayload.Should().Contain("\"timeout\":30");
    }

    [Fact]
    public async Task StopMachineAsync_without_options_sends_empty_body()
    {
        var (client, handler) = BuildClient();
        HttpRequestMessage? captured = null;
        SetupResponse(handler, HttpStatusCode.OK, "{\"ok\":true}", req => captured = req);

        await client.StopMachineAsync("m-3");

        captured!.RequestUri!.AbsoluteUri
            .Should().Be("https://api.machines.dev/v1/apps/test-app/machines/m-3/stop");

        // Empty JSON object — see FlyClient.StopMachineAsync for the rationale.
        var sentBody = await captured.Content!.ReadAsStringAsync();
        sentBody.Should().Be("{}");

        var op = await _db.FlyOperations.SingleAsync();
        op.Operation.Should().Be("StopMachine");
        op.Status.Should().Be(FlyOperationStatus.Succeeded);
        // No options were supplied. SendVoidAsync normalises an absent payload to
        // the valid-empty-JSON "{}" (see FlyClient.SendVoidAsync) so the audit row
        // always holds parseable JSON.
        op.RequestPayload.Should().Be("{}");
    }

    // ----------------------------------------------------------------------
    // SuspendMachine
    // ----------------------------------------------------------------------

    [Fact]
    public async Task SuspendMachineAsync_posts_to_correct_url()
    {
        var (client, handler) = BuildClient();
        HttpRequestMessage? captured = null;
        SetupResponse(handler, HttpStatusCode.OK, "{\"ok\":true}", req => captured = req);

        await client.SuspendMachineAsync("m-4", runtimeId: Guid.NewGuid());

        captured!.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri!.AbsoluteUri
            .Should().Be("https://api.machines.dev/v1/apps/test-app/machines/m-4/suspend");

        var op = await _db.FlyOperations.SingleAsync();
        op.Operation.Should().Be("SuspendMachine");
        op.Status.Should().Be(FlyOperationStatus.Succeeded);
    }

    // ----------------------------------------------------------------------
    // WaitForState
    // ----------------------------------------------------------------------

    [Fact]
    public async Task WaitForStateAsync_happy_path_returns_state()
    {
        var (client, handler) = BuildClient();
        var body = """
            {"state":"started","updated_at":"2026-05-08T10:00:00Z"}
            """;
        SetupResponse(handler, HttpStatusCode.OK, body);

        var state = await client.WaitForStateAsync("m-5", "started", TimeSpan.FromSeconds(10));

        state.State.Should().Be("started");
        state.UpdatedAt.Should().Be(new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc));

        var op = await _db.FlyOperations.SingleAsync();
        op.Operation.Should().Be("WaitForMachineState");
        op.Status.Should().Be(FlyOperationStatus.Succeeded);
    }

    [Fact]
    public async Task WaitForStateAsync_query_string_contains_target_and_timeout()
    {
        var (client, handler) = BuildClient();
        HttpRequestMessage? captured = null;
        var body = """
            {"state":"stopped","updated_at":"2026-05-08T10:01:00Z"}
            """;
        SetupResponse(handler, HttpStatusCode.OK, body, req => captured = req);

        await client.WaitForStateAsync("m-6", "stopped", TimeSpan.FromSeconds(45));

        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Get);
        var uri = captured.RequestUri!.AbsoluteUri;
        uri.Should().StartWith("https://api.machines.dev/v1/apps/test-app/machines/m-6/wait?");
        uri.Should().Contain("state=stopped");
        uri.Should().Contain("timeout=45");
    }

    [Fact]
    public async Task WaitForStateAsync_fly_timeout_throws_FlyApiException()
    {
        var (client, handler) = BuildClient();
        // Fly's machines API surfaces wait-timeouts as 408 (sometimes 504 behind the edge).
        // We surface that to the caller as a regular FlyApiException so retries can decide.
        SetupResponse(
            handler,
            HttpStatusCode.RequestTimeout,
            "{\"error\":\"timeout waiting for state\"}",
            flyRequestId: "req-wait-timeout");

        var act = async () => await client.WaitForStateAsync("m-7", "started", TimeSpan.FromSeconds(5));

        var ex = await act.Should().ThrowAsync<FlyApiException>();
        ex.Which.StatusCode.Should().Be(408);
        ex.Which.RequestId.Should().Be("req-wait-timeout");

        var op = await _db.FlyOperations.SingleAsync();
        op.Operation.Should().Be("WaitForMachineState");
        op.Status.Should().Be(FlyOperationStatus.Failed);
        op.HttpStatusCode.Should().Be(408);
    }

    // ----------------------------------------------------------------------
    // Cross-cutting: error mapping for state ops uses the same SendVoidAsync path
    // ----------------------------------------------------------------------

    [Fact]
    public async Task StartMachineAsync_on_409_writes_failed_row_and_throws()
    {
        var (client, handler) = BuildClient();
        SetupResponse(
            handler,
            HttpStatusCode.Conflict,
            "{\"error\":\"machine_not_stopped\"}",
            flyRequestId: "req-409");

        var act = async () => await client.StartMachineAsync("m-8");

        var ex = await act.Should().ThrowAsync<FlyApiException>();
        ex.Which.StatusCode.Should().Be(409);
        ex.Which.ErrorCode.Should().Be("machine_not_stopped");

        var op = await _db.FlyOperations.SingleAsync();
        op.Operation.Should().Be("StartMachine");
        op.Status.Should().Be(FlyOperationStatus.Failed);
        op.HttpStatusCode.Should().Be(409);
        op.ErrorCode.Should().Be("machine_not_stopped");
    }
}

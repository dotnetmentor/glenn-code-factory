using System.Net;
using System.Text;
using System.Text.Json;
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
/// Card 3 coverage: Machine CRUD methods on <see cref="FlyClient"/> plus the audit-log
/// behaviour of the typed <c>SendAsync&lt;T&gt;</c>. Each test verifies both the wire
/// shape (URL, method, body) and the side effect — every call should leave a single
/// <see cref="FlyOperation"/> row in the right state.
///
/// <para>We stay on the in-memory DbContext provider here: the EF idempotency query is
/// simple enough that Postgres-specific behaviour isn't load-bearing; the persistence
/// tests in <see cref="FlyOperationPersistenceTests"/> already prove the schema lines
/// up with the migration.</para>
/// </summary>
public class FlyClientMachineTests : IDisposable
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
    /// Stub the handler with a single canned response and capture the inbound request so
    /// the tests can assert on URL/method/body. Mirrors the helper in FlyClientTests but
    /// duplicated here so the two files don't need a shared base class — these are
    /// stand-alone enough that the small duplication beats the coupling.
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

    // ----------------------------------------------------------------------
    // CreateMachine
    // ----------------------------------------------------------------------

    [Fact]
    public async Task CreateMachine_returns_deserialized_machine_and_writes_succeeded_row()
    {
        var (client, handler) = BuildClient();
        HttpRequestMessage? captured = null;
        var responseBody = """
            {
                "id": "m-abc",
                "name": "rt-1",
                "state": "started",
                "region": "arn",
                "instance_id": "i-1",
                "private_ip": "fdaa::1",
                "created_at": "2026-05-08T10:00:00Z"
            }
            """;
        SetupResponse(handler, HttpStatusCode.OK, responseBody, req => captured = req, flyRequestId: "req-123");

        var req = new CreateMachineRequest(
            Name: "rt-1",
            Region: "arn",
            Config: new MachineConfig(Image: "registry.fly.io/glenn:latest"));

        var machine = await client.CreateMachineAsync(req, idempotencyKey: null, runtimeId: Guid.NewGuid());

        // Wire shape
        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri!.AbsoluteUri.Should().Be("https://api.machines.dev/v1/apps/test-app/machines");
        var sentBody = await captured.Content!.ReadAsStringAsync();
        sentBody.Should().Contain("\"name\":\"rt-1\"");
        sentBody.Should().Contain("\"region\":\"arn\"");
        // Snake-case serialiser must be in effect for nested config
        sentBody.Should().Contain("\"image\":\"registry.fly.io/glenn:latest\"");

        // Return value
        machine.Id.Should().Be("m-abc");
        machine.Name.Should().Be("rt-1");
        machine.State.Should().Be("started");
        machine.Region.Should().Be("arn");
        machine.InstanceId.Should().Be("i-1");
        machine.PrivateIp.Should().Be("fdaa::1");

        // Audit row
        var op = await _db.FlyOperations.SingleAsync();
        op.Operation.Should().Be("CreateMachine");
        op.Status.Should().Be(FlyOperationStatus.Succeeded);
        op.HttpStatusCode.Should().Be(200);
        op.RequestPayload.Should().Contain("\"name\":\"rt-1\"");
        op.ResponsePayload.Should().Contain("m-abc");
    }

    [Fact]
    public async Task CreateMachine_with_idempotency_key_skips_http_when_recent_succeeded_row_exists()
    {
        var (client, handler) = BuildClient();
        const string requestKey = "machineCreate:abc";

        // Pre-existing Succeeded row inside the 60s window with a deserialisable body.
        var cachedBody = """
            {
                "id": "m-cached",
                "name": "rt-cached",
                "state": "started",
                "region": "arn",
                "instance_id": null,
                "private_ip": null,
                "created_at": "2026-05-08T09:00:00Z"
            }
            """;
        _db.FlyOperations.Add(new FlyOperation
        {
            Id = Guid.NewGuid(),
            Operation = "CreateMachine",
            RequestKey = requestKey,
            RequestPayload = "{}",
            ResponsePayload = cachedBody,
            HttpStatusCode = 200,
            Status = FlyOperationStatus.Succeeded,
            LatencyMs = 100,
        });
        await _db.SaveChangesAsync();

        var req = new CreateMachineRequest("rt-1", "arn", new MachineConfig("img"));
        var machine = await client.CreateMachineAsync(req, idempotencyKey: requestKey);

        // Replayed from the cached row, NOT from HTTP.
        machine.Id.Should().Be("m-cached");
        machine.Name.Should().Be("rt-cached");

        // Strict mock — the handler being called would throw. Belt-and-braces verify too.
        handler.Protected().Verify(
            "SendAsync",
            Times.Never(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());

        // No new audit row — only the seed remains.
        (await _db.FlyOperations.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task CreateMachine_idempotency_falls_through_when_cached_row_is_older_than_60s()
    {
        var (client, handler) = BuildClient();
        const string requestKey = "machineCreate:old";

        // Seed a Succeeded row but stamp CreatedAt 61 seconds back so the window misses.
        var stale = new FlyOperation
        {
            Id = Guid.NewGuid(),
            Operation = "CreateMachine",
            RequestKey = requestKey,
            RequestPayload = "{}",
            ResponsePayload = "{}",
            HttpStatusCode = 200,
            Status = FlyOperationStatus.Succeeded,
            LatencyMs = 100,
        };
        _db.FlyOperations.Add(stale);
        await _db.SaveChangesAsync();

        // Backdate via tracked update — IAuditable touches UpdatedAt only.
        stale.CreatedAt = DateTime.UtcNow - TimeSpan.FromSeconds(61);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var responseBody = """
            {
                "id": "m-fresh",
                "name": "rt-fresh",
                "state": "started",
                "region": "arn",
                "instance_id": null,
                "private_ip": null,
                "created_at": "2026-05-08T10:00:00Z"
            }
            """;
        SetupResponse(handler, HttpStatusCode.OK, responseBody);

        var req = new CreateMachineRequest("rt-fresh", "arn", new MachineConfig("img"));
        var machine = await client.CreateMachineAsync(req, idempotencyKey: requestKey);

        machine.Id.Should().Be("m-fresh");
        // Old + new => 2 rows.
        (await _db.FlyOperations.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task CreateMachine_idempotency_skips_pending_and_failed_rows()
    {
        var (client, handler) = BuildClient();
        const string requestKey = "machineCreate:retry";

        // A Pending and a Failed row both within the window — neither should short-circuit.
        _db.FlyOperations.AddRange(
            new FlyOperation
            {
                Id = Guid.NewGuid(),
                Operation = "CreateMachine",
                RequestKey = requestKey,
                RequestPayload = "{}",
                Status = FlyOperationStatus.Pending,
            },
            new FlyOperation
            {
                Id = Guid.NewGuid(),
                Operation = "CreateMachine",
                RequestKey = requestKey,
                RequestPayload = "{}",
                ResponsePayload = "{\"error\":\"boom\"}",
                HttpStatusCode = 500,
                Status = FlyOperationStatus.Failed,
                ErrorCode = "boom",
            });
        await _db.SaveChangesAsync();

        var responseBody = """
            {"id":"m-x","name":"rt","state":"started","region":"arn","instance_id":null,"private_ip":null,"created_at":"2026-05-08T10:00:00Z"}
            """;
        SetupResponse(handler, HttpStatusCode.OK, responseBody);

        var req = new CreateMachineRequest("rt", "arn", new MachineConfig("img"));
        var machine = await client.CreateMachineAsync(req, idempotencyKey: requestKey);

        machine.Id.Should().Be("m-x");
        // Pending + Failed seed + new Succeeded = 3 rows.
        (await _db.FlyOperations.CountAsync()).Should().Be(3);
    }

    // ----------------------------------------------------------------------
    // GetMachine
    // ----------------------------------------------------------------------

    [Fact]
    public async Task GetMachineAsync_returns_machine_and_targets_correct_url()
    {
        var (client, handler) = BuildClient();
        HttpRequestMessage? captured = null;
        var body = """
            {"id":"m-1","name":"rt","state":"stopped","region":"arn","instance_id":"i-1","private_ip":"fdaa::2","created_at":"2026-05-08T10:00:00Z"}
            """;
        SetupResponse(handler, HttpStatusCode.OK, body, req => captured = req);

        var machine = await client.GetMachineAsync("m-1");

        captured!.Method.Should().Be(HttpMethod.Get);
        captured.RequestUri!.AbsoluteUri.Should().Be("https://api.machines.dev/v1/apps/test-app/machines/m-1");
        machine.Id.Should().Be("m-1");
        machine.State.Should().Be("stopped");
        machine.PrivateIp.Should().Be("fdaa::2");

        var op = await _db.FlyOperations.SingleAsync();
        op.Operation.Should().Be("GetMachine");
        op.Status.Should().Be(FlyOperationStatus.Succeeded);
    }

    // ----------------------------------------------------------------------
    // ListMachines
    // ----------------------------------------------------------------------

    [Fact]
    public async Task ListMachinesAsync_deserialises_array_response()
    {
        var (client, handler) = BuildClient();
        HttpRequestMessage? captured = null;
        var body = """
            [
                {"id":"m-1","name":"a","state":"started","region":"arn","instance_id":"i-1","private_ip":null,"created_at":"2026-05-08T10:00:00Z"},
                {"id":"m-2","name":"b","state":"stopped","region":"arn","instance_id":"i-2","private_ip":null,"created_at":"2026-05-08T10:01:00Z"}
            ]
            """;
        SetupResponse(handler, HttpStatusCode.OK, body, req => captured = req);

        var machines = await client.ListMachinesAsync();

        captured!.RequestUri!.AbsoluteUri.Should().Be("https://api.machines.dev/v1/apps/test-app/machines");
        machines.Should().HaveCount(2);
        machines[0].Id.Should().Be("m-1");
        machines[1].State.Should().Be("stopped");
    }

    // ----------------------------------------------------------------------
    // DestroyMachine
    // ----------------------------------------------------------------------

    [Fact]
    public async Task DestroyMachineAsync_with_force_appends_query_string()
    {
        var (client, handler) = BuildClient();
        HttpRequestMessage? captured = null;
        SetupResponse(handler, HttpStatusCode.OK, "{\"ok\":true}", req => captured = req);

        await client.DestroyMachineAsync("m-1", force: true, runtimeId: Guid.NewGuid());

        captured!.Method.Should().Be(HttpMethod.Delete);
        captured.RequestUri!.AbsoluteUri
            .Should().Be("https://api.machines.dev/v1/apps/test-app/machines/m-1?force=true");

        var op = await _db.FlyOperations.SingleAsync();
        op.Operation.Should().Be("DestroyMachine");
        op.Status.Should().Be(FlyOperationStatus.Succeeded);
    }

    [Fact]
    public async Task DestroyMachineAsync_without_force_omits_query_string()
    {
        var (client, handler) = BuildClient();
        HttpRequestMessage? captured = null;
        SetupResponse(handler, HttpStatusCode.OK, "{\"ok\":true}", req => captured = req);

        await client.DestroyMachineAsync("m-1");

        captured!.RequestUri!.AbsoluteUri
            .Should().Be("https://api.machines.dev/v1/apps/test-app/machines/m-1");
    }

    // ----------------------------------------------------------------------
    // Error mapping
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Send_429_throws_FlyApiException_carrying_status_and_request_id()
    {
        var (client, handler) = BuildClient();
        SetupResponse(
            handler,
            (HttpStatusCode)429,
            "{\"error\":\"rate_limited\",\"details\":\"too many\"}",
            flyRequestId: "req-rate");

        var act = async () => await client.GetMachineAsync("m-1");

        var ex = await act.Should().ThrowAsync<FlyApiException>();
        ex.Which.StatusCode.Should().Be(429);
        ex.Which.ErrorCode.Should().Be("rate_limited");
        ex.Which.RequestId.Should().Be("req-rate");
        ex.Which.Body.Should().Contain("too many");

        var op = await _db.FlyOperations.SingleAsync();
        op.Status.Should().Be(FlyOperationStatus.Failed);
        op.HttpStatusCode.Should().Be(429);
        op.ErrorCode.Should().Be("rate_limited");
        op.LatencyMs.Should().NotBeNull();
    }

    [Fact]
    public async Task Send_500_throws_FlyApiException_and_records_failed_row()
    {
        var (client, handler) = BuildClient();
        SetupResponse(
            handler,
            HttpStatusCode.InternalServerError,
            "{\"error\":\"internal\"}");

        var act = async () => await client.GetMachineAsync("m-1");

        var ex = await act.Should().ThrowAsync<FlyApiException>();
        ex.Which.StatusCode.Should().Be(500);
        ex.Which.ErrorCode.Should().Be("internal");

        var op = await _db.FlyOperations.SingleAsync();
        op.Status.Should().Be(FlyOperationStatus.Failed);
        op.HttpStatusCode.Should().Be(500);
        op.ResponsePayload.Should().Contain("internal");
    }

    [Fact]
    public async Task Send_with_unparseable_error_body_still_records_status_code()
    {
        // Fly occasionally returns a plain HTML page when we hit an edge during incidents.
        // We must not throw out of the error parser — the FlyApiException carries the raw body.
        var (client, handler) = BuildClient();
        SetupResponse(handler, HttpStatusCode.BadGateway, "<html>bad gateway</html>");

        var act = async () => await client.GetMachineAsync("m-1");

        var ex = await act.Should().ThrowAsync<FlyApiException>();
        ex.Which.StatusCode.Should().Be(502);
        ex.Which.ErrorCode.Should().BeNull();
        ex.Which.Body.Should().Contain("bad gateway");

        var op = await _db.FlyOperations.SingleAsync();
        op.Status.Should().Be(FlyOperationStatus.Failed);
        op.HttpStatusCode.Should().Be(502);
        op.ErrorCode.Should().BeNull();
    }
}

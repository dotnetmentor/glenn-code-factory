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
/// Card 5 coverage: Volume CRUD + extend on <see cref="FlyClient"/>. Mirrors the structure
/// of <see cref="FlyClientMachineTests"/> and <see cref="FlyClientStateTests"/>: every test
/// asserts the wire shape (URL, method, body), the deserialised return value where
/// applicable, and that exactly one <see cref="FlyOperation"/> row lands in the expected
/// status.
///
/// <para>One test is load-bearing for security rather than correctness:
/// <see cref="CreateVolumeAsync_AlwaysSendsEncryptedTrue"/> guards against any future
/// refactor that silently flips the default — runtime volumes hold customer data and we
/// never want one to land unencrypted because someone forgot a flag.</para>
/// </summary>
public class FlyClientVolumeTests : IDisposable
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

    /// <summary>
    /// A canonical Fly volume response body used across the happy-path tests. Pulled out
    /// so individual tests stay focused on the assertion that matters.
    /// </summary>
    private const string SampleVolumeBody = """
        {
            "id": "vol_abc",
            "name": "data-rt-1",
            "region": "arn",
            "size_gb": 10,
            "state": "ready",
            "attached_machine_id": null,
            "encrypted": true,
            "created_at": "2026-05-08T10:00:00Z"
        }
        """;

    // ----------------------------------------------------------------------
    // CreateVolume
    // ----------------------------------------------------------------------

    [Fact]
    public async Task CreateVolumeAsync_PostsToCorrectUrl_AndReturnsVolume()
    {
        var (client, handler) = BuildClient();
        HttpRequestMessage? captured = null;
        SetupResponse(handler, HttpStatusCode.OK, SampleVolumeBody, req => captured = req, flyRequestId: "req-vol-1");

        var req = new CreateVolumeRequest(Name: "data-rt-1", Region: "arn", SizeGb: 10);
        var volume = await client.CreateVolumeAsync(req, idempotencyKey: null, runtimeId: Guid.NewGuid());

        // Wire shape
        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri!.AbsoluteUri.Should().Be("https://api.machines.dev/v1/apps/test-app/volumes");
        var sentBody = await captured.Content!.ReadAsStringAsync();
        sentBody.Should().Contain("\"name\":\"data-rt-1\"");
        sentBody.Should().Contain("\"region\":\"arn\"");
        // Snake-case serialiser must be in effect on this slice too.
        sentBody.Should().Contain("\"size_gb\":10");

        // Return value
        volume.Id.Should().Be("vol_abc");
        volume.Name.Should().Be("data-rt-1");
        volume.Region.Should().Be("arn");
        volume.SizeGb.Should().Be(10);
        volume.State.Should().Be("ready");
        volume.AttachedMachineId.Should().BeNull();
        volume.Encrypted.Should().BeTrue();

        // Audit row
        var op = await _db.FlyOperations.SingleAsync();
        op.Operation.Should().Be("CreateVolume");
        op.Status.Should().Be(FlyOperationStatus.Succeeded);
        op.HttpStatusCode.Should().Be(200);
        op.RequestPayload.Should().Contain("\"name\":\"data-rt-1\"");
        op.ResponsePayload.Should().Contain("vol_abc");
    }

    /// <summary>
    /// Security regression test: even if a caller constructs <see cref="CreateVolumeRequest"/>
    /// with the default constructor — i.e. without explicitly passing <c>encrypted: true</c> —
    /// the JSON body that lands on the wire MUST contain <c>"encrypted":true</c>. If this
    /// breaks, runtime volumes can ship unencrypted. Treat a failure here as a security incident,
    /// not a flaky test.
    /// </summary>
    [Fact]
    public async Task CreateVolumeAsync_AlwaysSendsEncryptedTrue()
    {
        var (client, handler) = BuildClient();
        HttpRequestMessage? captured = null;
        SetupResponse(handler, HttpStatusCode.OK, SampleVolumeBody, req => captured = req);

        // Caller does NOT specify encrypted — the default must take care of it.
        var req = new CreateVolumeRequest(Name: "data-default", Region: "arn", SizeGb: 5);
        await client.CreateVolumeAsync(req);

        captured.Should().NotBeNull();
        var sentBody = await captured!.Content!.ReadAsStringAsync();
        sentBody.Should().Contain("\"encrypted\":true",
            "runtime volumes must always be encrypted by default — see CreateVolumeRequest docs");

        // Belt-and-braces: the audit row's persisted payload must show the same.
        var op = await _db.FlyOperations.SingleAsync();
        op.RequestPayload.Should().Contain("\"encrypted\":true");
    }

    [Fact]
    public async Task CreateVolumeAsync_Idempotency_SkipsHttpCallWithinWindow()
    {
        var (client, handler) = BuildClient();
        const string requestKey = "volumeCreate:abc";

        // Pre-existing Succeeded row inside the 60s window with a deserialisable body.
        _db.FlyOperations.Add(new FlyOperation
        {
            Id = Guid.NewGuid(),
            Operation = "CreateVolume",
            RequestKey = requestKey,
            RequestPayload = "{}",
            ResponsePayload = SampleVolumeBody,
            HttpStatusCode = 200,
            Status = FlyOperationStatus.Succeeded,
            LatencyMs = 100,
        });
        await _db.SaveChangesAsync();

        var req = new CreateVolumeRequest("data-rt-1", "arn", 10);
        var volume = await client.CreateVolumeAsync(req, idempotencyKey: requestKey);

        // Replayed from cached row, NOT from HTTP.
        volume.Id.Should().Be("vol_abc");

        // Strict mock — the handler being called would throw. Belt-and-braces verify too.
        handler.Protected().Verify(
            "SendAsync",
            Times.Never(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());

        // No new audit row — only the seed remains.
        (await _db.FlyOperations.CountAsync()).Should().Be(1);
    }

    // ----------------------------------------------------------------------
    // GetVolume
    // ----------------------------------------------------------------------

    [Fact]
    public async Task GetVolumeAsync_ReturnsVolume()
    {
        var (client, handler) = BuildClient();
        HttpRequestMessage? captured = null;
        SetupResponse(handler, HttpStatusCode.OK, SampleVolumeBody, req => captured = req);

        var volume = await client.GetVolumeAsync("vol_abc");

        captured!.Method.Should().Be(HttpMethod.Get);
        captured.RequestUri!.AbsoluteUri.Should().Be("https://api.machines.dev/v1/apps/test-app/volumes/vol_abc");
        volume.Id.Should().Be("vol_abc");
        volume.SizeGb.Should().Be(10);
        volume.State.Should().Be("ready");
        volume.Encrypted.Should().BeTrue();

        var op = await _db.FlyOperations.SingleAsync();
        op.Operation.Should().Be("GetVolume");
        op.Status.Should().Be(FlyOperationStatus.Succeeded);
    }

    // ----------------------------------------------------------------------
    // ListVolumes
    // ----------------------------------------------------------------------

    [Fact]
    public async Task ListVolumesAsync_ReturnsList()
    {
        var (client, handler) = BuildClient();
        HttpRequestMessage? captured = null;
        var body = """
            [
                {"id":"vol_a","name":"a","region":"arn","size_gb":1,"state":"ready","attached_machine_id":null,"encrypted":true,"created_at":"2026-05-08T10:00:00Z"},
                {"id":"vol_b","name":"b","region":"arn","size_gb":2,"state":"ready","attached_machine_id":"m-1","encrypted":true,"created_at":"2026-05-08T10:01:00Z"}
            ]
            """;
        SetupResponse(handler, HttpStatusCode.OK, body, req => captured = req);

        var volumes = await client.ListVolumesAsync();

        captured!.Method.Should().Be(HttpMethod.Get);
        captured.RequestUri!.AbsoluteUri.Should().Be("https://api.machines.dev/v1/apps/test-app/volumes");
        volumes.Should().HaveCount(2);
        volumes[0].Id.Should().Be("vol_a");
        volumes[0].AttachedMachineId.Should().BeNull();
        volumes[1].Id.Should().Be("vol_b");
        volumes[1].AttachedMachineId.Should().Be("m-1");
        volumes[1].SizeGb.Should().Be(2);

        var op = await _db.FlyOperations.SingleAsync();
        op.Operation.Should().Be("ListVolumes");
        op.Status.Should().Be(FlyOperationStatus.Succeeded);
    }

    // ----------------------------------------------------------------------
    // DestroyVolume
    // ----------------------------------------------------------------------

    [Fact]
    public async Task DestroyVolumeAsync_DeletesAtCorrectUrl_AndWritesAuditRow()
    {
        var (client, handler) = BuildClient();
        HttpRequestMessage? captured = null;
        SetupResponse(handler, HttpStatusCode.OK, "{\"ok\":true}", req => captured = req);

        var runtimeId = Guid.NewGuid();
        await client.DestroyVolumeAsync("vol_abc", runtimeId: runtimeId);

        captured!.Method.Should().Be(HttpMethod.Delete);
        captured.RequestUri!.AbsoluteUri
            .Should().Be("https://api.machines.dev/v1/apps/test-app/volumes/vol_abc");

        var op = await _db.FlyOperations.SingleAsync();
        op.Operation.Should().Be("DestroyVolume");
        op.Status.Should().Be(FlyOperationStatus.Succeeded);
        op.HttpStatusCode.Should().Be(200);
        op.RuntimeId.Should().Be(runtimeId);
    }

    // ----------------------------------------------------------------------
    // ExtendVolume
    // ----------------------------------------------------------------------

    [Fact]
    public async Task ExtendVolumeAsync_PutsToExtendEndpoint_WithSizeBody()
    {
        var (client, handler) = BuildClient();
        HttpRequestMessage? captured = null;
        // Fly returns the updated volume; we surface it directly to the caller.
        var grownBody = """
            {
                "id": "vol_abc",
                "name": "data-rt-1",
                "region": "arn",
                "size_gb": 25,
                "state": "ready",
                "attached_machine_id": null,
                "encrypted": true,
                "created_at": "2026-05-08T10:00:00Z"
            }
            """;
        SetupResponse(handler, HttpStatusCode.OK, grownBody, req => captured = req);

        var volume = await client.ExtendVolumeAsync("vol_abc", newSizeGb: 25, runtimeId: Guid.NewGuid());

        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Put);
        captured.RequestUri!.AbsoluteUri
            .Should().Be("https://api.machines.dev/v1/apps/test-app/volumes/vol_abc/extend");

        var sentBody = await captured.Content!.ReadAsStringAsync();
        sentBody.Should().Be("{\"size_gb\":25}");

        // Returned volume reflects the new size — caller can use it without a follow-up GET.
        volume.SizeGb.Should().Be(25);

        var op = await _db.FlyOperations.SingleAsync();
        op.Operation.Should().Be("ExtendVolume");
        op.Status.Should().Be(FlyOperationStatus.Succeeded);
        op.RequestPayload.Should().Contain("\"size_gb\":25");
    }

    // ----------------------------------------------------------------------
    // Error mapping (volume slice uses the same SendAsync / SendVoidAsync pipeline,
    // but a sanity check on a volume op locks in the wiring).
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Send_500_OnVolumeOp_WritesFailedAuditRow()
    {
        var (client, handler) = BuildClient();
        SetupResponse(
            handler,
            HttpStatusCode.InternalServerError,
            "{\"error\":\"internal\"}",
            flyRequestId: "req-vol-500");

        var act = async () => await client.GetVolumeAsync("vol_abc");

        var ex = await act.Should().ThrowAsync<FlyApiException>();
        ex.Which.StatusCode.Should().Be(500);
        ex.Which.ErrorCode.Should().Be("internal");
        ex.Which.RequestId.Should().Be("req-vol-500");

        var op = await _db.FlyOperations.SingleAsync();
        op.Operation.Should().Be("GetVolume");
        op.Status.Should().Be(FlyOperationStatus.Failed);
        op.HttpStatusCode.Should().Be(500);
        op.ErrorCode.Should().Be("internal");
        op.ResponsePayload.Should().Contain("internal");
    }
}

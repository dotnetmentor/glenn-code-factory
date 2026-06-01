using System.Net;
using System.Text;
using Api.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Source.Features.FlyManagement;
using Source.Features.FlyManagement.Configuration;
using Source.Infrastructure;

namespace Api.Tests.Features.FlyManagement;

/// <summary>
/// Foundation-level coverage for <see cref="FlyClient"/> per Card 2: header stamping,
/// base address routing, and the <see cref="FlyClient.PingAsync"/> probe behaviour.
/// Machine CRUD coverage lives in <see cref="FlyClientMachineTests"/>.
/// </summary>
public class FlyClientTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDbContextFactory.Create();

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private static readonly FlyOptions DefaultOptions = new()
    {
        ApiToken = "fly_pat_secret_xyz",
        OrgSlug = "personal",
        AppName = "test-app",
        DefaultRegion = "arn",
    };

    /// <summary>
    /// Build a FlyClient whose HttpClient is wired to the given message handler. The
    /// BaseAddress mirrors what AddFlyManagement configures in DI.
    /// </summary>
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
        Action<HttpRequestMessage>? capture = null,
        string body = "{}")
    {
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capture?.Invoke(req))
            .ReturnsAsync(() => new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    [Fact]
    public async Task SendAsync_stamps_bearer_authorization_from_options()
    {
        var (client, handler) = BuildClient();
        HttpRequestMessage? captured = null;
        SetupResponse(handler, HttpStatusCode.OK, req => captured = req);

        using var request = new HttpRequestMessage(HttpMethod.Get, "apps/test-app");
        using var _ = await client.SendAsync(request, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.Headers.Authorization.Should().NotBeNull();
        captured.Headers.Authorization!.Scheme.Should().Be("Bearer");
        captured.Headers.Authorization.Parameter.Should().Be("fly_pat_secret_xyz");
    }

    [Fact]
    public async Task SendAsync_stamps_user_agent_header()
    {
        var (client, handler) = BuildClient();
        HttpRequestMessage? captured = null;
        SetupResponse(handler, HttpStatusCode.OK, req => captured = req);

        using var request = new HttpRequestMessage(HttpMethod.Get, "apps/test-app");
        using var _ = await client.SendAsync(request, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.Headers.UserAgent.ToString().Should().Be("glenn-platform/1.0");
    }

    [Fact]
    public async Task SendAsync_resolves_relative_uri_against_machines_dev_base_address()
    {
        var (client, handler) = BuildClient();
        HttpRequestMessage? captured = null;
        SetupResponse(handler, HttpStatusCode.OK, req => captured = req);

        using var request = new HttpRequestMessage(HttpMethod.Get, "apps/test-app");
        using var _ = await client.SendAsync(request, CancellationToken.None);

        captured!.RequestUri!.AbsoluteUri.Should().Be("https://api.machines.dev/v1/apps/test-app");
    }

    [Fact]
    public async Task PingAsync_returns_true_for_200_response()
    {
        var (client, handler) = BuildClient();
        SetupResponse(handler, HttpStatusCode.OK);

        var result = await client.PingAsync(CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task PingAsync_returns_true_for_404_app_missing_but_auth_works()
    {
        var (client, handler) = BuildClient();
        SetupResponse(handler, HttpStatusCode.NotFound);

        var result = await client.PingAsync(CancellationToken.None);

        // 404 means the API reached us and the token authenticated — it just hasn't
        // created the app yet. From a configuration-probe POV this is success.
        result.Should().BeTrue();
    }

    [Fact]
    public async Task PingAsync_returns_false_for_500_server_error()
    {
        var (client, handler) = BuildClient();
        SetupResponse(handler, HttpStatusCode.InternalServerError);

        var result = await client.PingAsync(CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task PingAsync_returns_false_for_401_unauthorized()
    {
        var (client, handler) = BuildClient();
        SetupResponse(handler, HttpStatusCode.Unauthorized);

        var result = await client.PingAsync(CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task PingAsync_returns_false_when_app_name_is_empty()
    {
        var (client, handler) = BuildClient(new FlyOptions
        {
            ApiToken = "tok",
            AppName = "",
        });

        var result = await client.PingAsync(CancellationToken.None);

        // Strict mock — if the handler had been called, this would throw. Verifying
        // that we short-circuit before any HTTP traffic.
        result.Should().BeFalse();
        handler.Protected().Verify(
            "SendAsync",
            Times.Never(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task PingAsync_targets_apps_endpoint_with_configured_app_name()
    {
        var (client, handler) = BuildClient(new FlyOptions
        {
            ApiToken = "tok",
            AppName = "glenn-runtimes",
        });
        HttpRequestMessage? captured = null;
        SetupResponse(handler, HttpStatusCode.OK, req => captured = req);

        await client.PingAsync(CancellationToken.None);

        captured!.Method.Should().Be(HttpMethod.Get);
        captured.RequestUri!.AbsoluteUri.Should().Be("https://api.machines.dev/v1/apps/glenn-runtimes");
    }

    [Fact]
    public async Task PingAsync_returns_false_when_transport_throws()
    {
        var (client, handler) = BuildClient();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("network down"));

        var result = await client.PingAsync(CancellationToken.None);

        result.Should().BeFalse();
    }
}

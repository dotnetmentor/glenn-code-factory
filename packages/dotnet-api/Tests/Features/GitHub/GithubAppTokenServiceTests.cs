using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Source.Features.GitHub.Configuration;
using Source.Features.GitHub.Services;

namespace Api.Tests.Features.GitHub;

/// <summary>
/// Unit tests for <see cref="GithubAppTokenService"/>.
/// Covers App-JWT shape (alg/iss/exp) and the in-memory cache around installation tokens.
/// HTTP is stubbed via <c>Mock&lt;HttpMessageHandler&gt;</c>.
/// </summary>
public class GithubAppTokenServiceTests
{
    // ----- helpers ---------------------------------------------------------

    private static (GithubOptions options, string privateKeyPem) CreateOptionsWithFreshKey(string appId = "12345")
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportRSAPrivateKeyPem();
        return (new GithubOptions
        {
            AppId = appId,
            PrivateKeyPem = pem,
        }, pem);
    }

    private static IGithubAppTokenService BuildService(
        GithubOptions options,
        IHttpClientFactory factory,
        IMemoryCache? cache = null)
    {
        cache ??= new MemoryCache(new MemoryCacheOptions());
        return new GithubAppTokenService(
            new StubGithubOptionsAccessor(options),
            cache,
            factory,
            new Mock<ILogger<GithubAppTokenService>>().Object);
    }

    private static IHttpClientFactory FactoryFor(HttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("https://api.github.com/")
            });
        return factory.Object;
    }

    // ----- CreateAppJwt ----------------------------------------------------

    [Fact]
    public void CreateAppJwt_produces_RS256_token_with_correct_iss_and_lifetime()
    {
        var (options, _) = CreateOptionsWithFreshKey(appId: "test-app-id");
        var factory = FactoryFor(new Mock<HttpMessageHandler>().Object);
        var service = BuildService(options, factory);

        var jwt = service.CreateAppJwt();

        jwt.Should().NotBeNullOrWhiteSpace();

        var parsed = new JwtSecurityTokenHandler().ReadJwtToken(jwt);
        parsed.Header.Alg.Should().Be("RS256");
        parsed.Issuer.Should().Be("test-app-id");

        // Lifetime should be roughly 10 minutes (with 60s clock-skew hedge on each end).
        var lifetime = parsed.ValidTo - parsed.IssuedAt;
        lifetime.Should().BeCloseTo(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(60));

        // iat should be slightly in the past — the implementation backdates it 60s.
        parsed.IssuedAt.Should().BeBefore(DateTime.UtcNow.AddSeconds(1));
    }

    // ----- GetInstallationTokenAsync caching -------------------------------

    [Fact]
    public async Task GetInstallationTokenAsync_caches_token_within_window_so_HTTP_called_once_per_two_calls()
    {
        var (options, _) = CreateOptionsWithFreshKey();
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);

        // GitHub returns 1h-from-now expiry — well within our 50-min cache window.
        var expiresAt = DateTime.UtcNow.AddHours(1).ToString("O");
        var responseJson = JsonSerializer.Serialize(new
        {
            token = "ghs_fake_installation_token",
            expires_at = DateTime.UtcNow.AddHours(1),
        });

        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            });

        var factory = FactoryFor(handler.Object);
        var service = BuildService(options, factory);

        var t1 = await service.GetInstallationTokenAsync(99L);
        var t2 = await service.GetInstallationTokenAsync(99L);

        t1.Should().Be("ghs_fake_installation_token");
        t2.Should().Be(t1);

        handler.Protected().Verify(
            "SendAsync",
            Times.Exactly(1),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GetInstallationTokenAsync_separate_installations_get_separate_HTTP_calls()
    {
        var (options, _) = CreateOptionsWithFreshKey();
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);

        var responseJson = JsonSerializer.Serialize(new
        {
            token = "ghs_token_per_install",
            expires_at = DateTime.UtcNow.AddHours(1),
        });

        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            });

        var factory = FactoryFor(handler.Object);
        var service = BuildService(options, factory);

        await service.GetInstallationTokenAsync(1L);
        await service.GetInstallationTokenAsync(2L);

        handler.Protected().Verify(
            "SendAsync",
            Times.Exactly(2),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GetInstallationTokenAsync_sends_App_JWT_as_bearer_and_correct_path()
    {
        var (options, _) = CreateOptionsWithFreshKey();
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);

        HttpRequestMessage? captured = null;
        var responseJson = JsonSerializer.Serialize(new
        {
            token = "ghs_x",
            expires_at = DateTime.UtcNow.AddHours(1),
        });

        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            });

        var factory = FactoryFor(handler.Object);
        var service = BuildService(options, factory);

        await service.GetInstallationTokenAsync(777L);

        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri!.AbsolutePath.Should().Be("/app/installations/777/access_tokens");
        captured.Headers.Authorization.Should().NotBeNull();
        captured.Headers.Authorization!.Scheme.Should().Be("Bearer");
        captured.Headers.Authorization.Parameter.Should().NotBeNullOrEmpty();
    }
}

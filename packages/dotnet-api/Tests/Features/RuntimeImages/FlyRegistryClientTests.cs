using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Source.Features.FlyManagement.Configuration;
using Source.Features.RuntimeImages.Services;

namespace Api.Tests.Features.RuntimeImages;

/// <summary>
/// Unit tests for <see cref="FlyRegistryClient"/>. We never hit the real Fly registry —
/// instead we substitute a scripted <see cref="HttpMessageHandler"/> below
/// <see cref="IHttpClientFactory"/> and assert the client parses OCI v2 responses
/// correctly, propagates auth headers, and classifies failures into typed
/// <see cref="FlyRegistryException"/>s.
/// </summary>
public class FlyRegistryClientTests
{
    private const string FlyToken = "fly_test_token_xyz";

    private static FlyOptions DefaultOptions() => new()
    {
        ApiToken = FlyToken,
        OrgSlug = "personal",
        AppName = "test-app",
        DefaultRegion = "arn",
    };

    /// <summary>
    /// Build a client wired to the provided <paramref name="handler"/>. Mirrors what
    /// <c>AddRuntimeImagesFeature</c> registers in DI, but we drive the named
    /// <see cref="IHttpClientFactory"/> with a hand-rolled stub so the test is fully
    /// in-process.
    /// </summary>
    private static FlyRegistryClient BuildClient(Mock<HttpMessageHandler> handler, FlyOptions? options = null)
    {
        var http = new HttpClient(handler.Object, disposeHandler: false)
        {
            BaseAddress = new Uri("https://registry.fly.io"),
        };
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        factory.Setup(f => f.CreateClient(FlyRegistryClient.HttpClientName)).Returns(http);

        var optsAccessor = new Mock<IFlyOptionsAccessor>();
        optsAccessor.SetupGet(o => o.Current).Returns(options ?? DefaultOptions());

        return new FlyRegistryClient(
            factory.Object,
            optsAccessor.Object,
            new Mock<ILogger<FlyRegistryClient>>().Object);
    }

    private static void SetupResponse(
        Mock<HttpMessageHandler> handler,
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        Action<HttpRequestMessage>? capture = null)
    {
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capture?.Invoke(req))
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) => respond(req));
    }

    // ----------------------------------------------------------------------
    // ListTagsAsync
    // ----------------------------------------------------------------------

    [Fact]
    public async Task ListTags_parses_oci_v2_response_and_stamps_bearer_token()
    {
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        HttpRequestMessage? captured = null;
        SetupResponse(handler, _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"name\":\"glenn-runtime-base\",\"tags\":[\"2026.05.08-7af3b21\",\"2026.05.07-deadbee\"]}",
                Encoding.UTF8,
                "application/json"),
        }, req => captured = req);

        var client = BuildClient(handler);
        var tags = await client.ListTagsAsync("glenn-runtime-base", CancellationToken.None);

        tags.Should().BeEquivalentTo(new[] { "2026.05.08-7af3b21", "2026.05.07-deadbee" });

        captured.Should().NotBeNull();
        captured!.RequestUri!.AbsolutePath.Should().Be("/v2/glenn-runtime-base/tags/list");
        // The registry uses HTTP Basic auth with username "x" and the Fly token
        // as the password (Docker-registry convention), not a Bearer token.
        captured.Headers.Authorization!.Scheme.Should().Be("Basic");
        captured.Headers.Authorization.Parameter.Should()
            .Be(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"x:{FlyToken}")));
    }

    [Fact]
    public async Task ListTags_returns_empty_when_tags_field_absent()
    {
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        SetupResponse(handler, _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"name\":\"some-image\"}", Encoding.UTF8, "application/json"),
        });

        var client = BuildClient(handler);
        var tags = await client.ListTagsAsync("some-image", CancellationToken.None);

        tags.Should().BeEmpty();
    }

    [Fact]
    public async Task ListTags_throws_NotFound_on_404()
    {
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        SetupResponse(handler, _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{\"errors\":[{\"code\":\"NAME_UNKNOWN\"}]}", Encoding.UTF8, "application/json"),
        });

        var client = BuildClient(handler);

        var ex = await Assert.ThrowsAsync<FlyRegistryException>(
            () => client.ListTagsAsync("missing", CancellationToken.None));
        ex.Kind.Should().Be(FlyRegistryErrorKind.NotFound);
    }

    [Fact]
    public async Task ListTags_throws_Unauthorized_on_401()
    {
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        SetupResponse(handler, _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });

        var client = BuildClient(handler);

        var ex = await Assert.ThrowsAsync<FlyRegistryException>(
            () => client.ListTagsAsync("img", CancellationToken.None));
        ex.Kind.Should().Be(FlyRegistryErrorKind.Unauthorized);
    }

    [Fact]
    public async Task ListTags_throws_Transport_when_http_layer_fails()
    {
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("DNS failure"));

        var client = BuildClient(handler);

        var ex = await Assert.ThrowsAsync<FlyRegistryException>(
            () => client.ListTagsAsync("img", CancellationToken.None));
        ex.Kind.Should().Be(FlyRegistryErrorKind.Transport);
    }

    [Fact]
    public async Task ListTags_throws_Unauthorized_when_token_missing()
    {
        // Empty token must not even leave the building — surface the misconfiguration
        // immediately rather than confuse it with a real Fly 401.
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        var emptyOptions = new FlyOptions { ApiToken = string.Empty, AppName = "x", OrgSlug = "y" };

        var client = BuildClient(handler, emptyOptions);

        var ex = await Assert.ThrowsAsync<FlyRegistryException>(
            () => client.ListTagsAsync("img", CancellationToken.None));
        ex.Kind.Should().Be(FlyRegistryErrorKind.Unauthorized);
    }

    // ----------------------------------------------------------------------
    // GetManifestAsync
    // ----------------------------------------------------------------------

    [Fact]
    public async Task GetManifest_parses_digest_size_created_and_labels()
    {
        // The real OCI v2 manifest endpoint returns the manifest digest in the
        // Docker-Content-Digest header (not the body). The manifest body carries the
        // config descriptor (digest + size); the config blob (a separate GET) has the
        // build timestamp and labels. We script both responses here.
        const string ManifestDigest = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
        const string ConfigDigest = "sha256:2222222222222222222222222222222222222222222222222222222222222222";
        const long ConfigSize = 4711L;

        var manifestBody = "{\"schemaVersion\":2," +
                           "\"mediaType\":\"application/vnd.oci.image.manifest.v1+json\"," +
                           "\"config\":{\"mediaType\":\"application/vnd.oci.image.config.v1+json\"," +
                           $"\"digest\":\"{ConfigDigest}\",\"size\":{ConfigSize}}}," +
                           "\"layers\":[]}";

        var configBlobBody = "{\"created\":\"2026-05-08T12:34:56Z\"," +
                             "\"architecture\":\"amd64\"," +
                             "\"config\":{\"Labels\":{\"org.opencontainers.image.revision\":\"7af3b21\"," +
                             "\"org.opencontainers.image.source\":\"https://github.com/x/y\"}}}";

        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        var requests = new List<HttpRequestMessage>();
        SetupResponse(handler, req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/manifests/"))
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(manifestBody, Encoding.UTF8, "application/vnd.oci.image.manifest.v1+json"),
                };
                resp.Headers.Add("Docker-Content-Digest", ManifestDigest);
                return resp;
            }
            // Config blob.
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(configBlobBody, Encoding.UTF8, "application/json"),
            };
        }, req => requests.Add(req));

        var client = BuildClient(handler);
        var info = await client.GetManifestAsync("glenn-runtime-base", "2026.05.08-7af3b21", CancellationToken.None);

        info.Digest.Should().Be(ManifestDigest);
        info.SizeBytes.Should().Be(ConfigSize);
        info.PushedAt.Should().Be(new DateTime(2026, 5, 8, 12, 34, 56, DateTimeKind.Utc));
        info.Labels.Should().ContainKey(FlyRegistryClient.GitShaLabelKey)
            .WhoseValue.Should().Be("7af3b21");

        // Both calls must have run.
        requests.Should().HaveCount(2);
        requests[0].RequestUri!.AbsolutePath.Should().Be("/v2/glenn-runtime-base/manifests/2026.05.08-7af3b21");
        requests[1].RequestUri!.AbsolutePath.Should().Be($"/v2/glenn-runtime-base/blobs/{ConfigDigest}");

        // Manifest fetch must declare the OCI media types so Fly serves the right shape.
        requests[0].Headers.Accept.Select(a => a.MediaType).Should()
            .Contain("application/vnd.oci.image.manifest.v1+json")
            .And.Contain("application/vnd.docker.distribution.manifest.v2+json");
    }

    [Fact]
    public async Task GetManifest_falls_back_to_config_digest_when_header_missing()
    {
        // Some legacy paths don't echo Docker-Content-Digest; we use the config digest
        // so callers always get a non-empty value.
        const string ConfigDigest = "sha256:cafef00dcafef00dcafef00dcafef00dcafef00dcafef00dcafef00dcafef00d";
        var manifestBody = $"{{\"schemaVersion\":2,\"config\":{{\"digest\":\"{ConfigDigest}\",\"size\":100}},\"layers\":[]}}";

        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        SetupResponse(handler, req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/manifests/"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(manifestBody, Encoding.UTF8, "application/json"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        });

        var client = BuildClient(handler);
        var info = await client.GetManifestAsync("img", "v1", CancellationToken.None);

        info.Digest.Should().Be(ConfigDigest);
        info.PushedAt.Should().BeNull();
        info.Labels.Should().BeEmpty();
    }

    [Fact]
    public async Task GetManifest_throws_Protocol_when_config_digest_missing()
    {
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        SetupResponse(handler, _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            // No "config" object at all.
            Content = new StringContent("{\"schemaVersion\":2,\"layers\":[]}", Encoding.UTF8, "application/json"),
        });

        var client = BuildClient(handler);

        var ex = await Assert.ThrowsAsync<FlyRegistryException>(
            () => client.GetManifestAsync("img", "v1", CancellationToken.None));
        ex.Kind.Should().Be(FlyRegistryErrorKind.Protocol);
    }
}

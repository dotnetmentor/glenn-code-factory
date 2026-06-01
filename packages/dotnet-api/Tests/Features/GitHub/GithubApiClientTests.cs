using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Source.Features.GitHub.Configuration;
using Source.Features.GitHub.Services;

namespace Api.Tests.Features.GitHub;

/// <summary>
/// Light coverage for <see cref="GithubApiClient"/>: a happy-path mapping for
/// <see cref="GithubApiClient.ListInstallationRepositoriesAsync"/>. The handler is
/// stubbed to short-circuit the pagination loop after one page.
/// </summary>
public class GithubApiClientTests
{
    [Fact]
    public async Task ListInstallationRepositoriesAsync_maps_response_into_repo_dtos()
    {
        var responseJson = JsonSerializer.Serialize(new
        {
            total_count = 1,
            repositories = new[]
            {
                new
                {
                    id = 42L,
                    name = "robot",
                    full_name = "octo/robot",
                    @private = true,
                    default_branch = "main",
                    owner = new { id = 7L, login = "octo", type = "Organization", avatar_url = (string?)null },
                },
            },
        });

        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            });

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler.Object, disposeHandler: false)
            {
                BaseAddress = new Uri("https://api.github.com/"),
            });

        var tokenService = new Mock<IGithubAppTokenService>();
        tokenService.Setup(t => t.GetInstallationTokenAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghs_test_token");

        var client = new GithubApiClient(
            factory.Object,
            tokenService.Object,
            new StubGithubOptionsAccessor(new GithubOptions()),
            new Mock<ILogger<GithubApiClient>>().Object);

        var repos = await client.ListInstallationRepositoriesAsync(99L);

        repos.Should().HaveCount(1);
        repos[0].Id.Should().Be(42L);
        repos[0].FullName.Should().Be("octo/robot");
        repos[0].Private.Should().BeTrue();
        repos[0].DefaultBranch.Should().Be("main");
        repos[0].Owner.Login.Should().Be("octo");
        repos[0].Owner.Type.Should().Be("Organization");
    }
}

using Source.Features.DaemonVersions.Queries.ResolveDaemonVersion;

namespace Api.Tests.Features.DaemonVersions;

public class ResolveDaemonVersionHandlerTests
{
    [Theory]
    [InlineData("/api/files/daemon-bundles/foo.tar.gz", "https://tunnel.trycloudflare.com", "https://tunnel.trycloudflare.com/api/files/daemon-bundles/foo.tar.gz")]
    [InlineData("https://cdn.example.com/foo.tar.gz", "https://tunnel.trycloudflare.com", "https://cdn.example.com/foo.tar.gz")]
    [InlineData("/api/files/foo.tar.gz", "https://tunnel.trycloudflare.com/", "https://tunnel.trycloudflare.com/api/files/foo.tar.gz")]
    [InlineData("/api/files/foo.tar.gz", "", "/api/files/foo.tar.gz")]
    public void ToAbsoluteDownloadUrl_prepends_public_api_url_for_relative_paths(
        string downloadUrl,
        string publicApiUrl,
        string expected)
    {
        ResolveDaemonVersionHandler.ToAbsoluteDownloadUrl(downloadUrl, publicApiUrl)
            .Should().Be(expected);
    }
}

using Source.Features.GitHub;

namespace Api.Tests.Features.GitHub;

public class WorkspaceFrontendRoutesTests
{
    [Fact]
    public void Home_returns_workspace_root_path()
    {
        WorkspaceFrontendRoutes.Home("acme-corp").Should().Be("/w/acme-corp");
    }

    [Theory]
    [InlineData("install", "success", "/w/acme?install=success")]
    [InlineData("install", "pending", "/w/acme?install=pending")]
    [InlineData("reauth", "error", "/w/acme?reauth=error")]
    public void HomeWithQuery_builds_callback_redirect_paths(string key, string value, string expected)
    {
        WorkspaceFrontendRoutes.HomeWithQuery("acme", key, value).Should().Be(expected);
    }
}

using Source.Features.GitHub.Configuration;

namespace Api.Tests.Features.GitHub;

/// <summary>
/// Minimal in-memory stand-in for <see cref="IGithubOptionsAccessor"/> in unit tests.
/// Replaces the old <c>Options.Create(new GithubOptions { ... })</c> pattern that worked when
/// services took <c>IOptions&lt;GithubOptions&gt;</c> directly.
/// </summary>
internal sealed class StubGithubOptionsAccessor : IGithubOptionsAccessor
{
    public StubGithubOptionsAccessor(GithubOptions options) => Current = options;
    public GithubOptions Current { get; }
}

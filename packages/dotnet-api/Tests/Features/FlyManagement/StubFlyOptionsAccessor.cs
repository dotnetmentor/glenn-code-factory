using Source.Features.FlyManagement.Configuration;

namespace Api.Tests.Features.FlyManagement;

/// <summary>
/// Minimal in-memory stand-in for <see cref="IFlyOptionsAccessor"/> in unit tests.
/// Mirrors <c>StubGithubOptionsAccessor</c> — the SystemSettings round-trip is covered
/// by its own tests, so FlyClient tests don't need a real DB.
/// </summary>
internal sealed class StubFlyOptionsAccessor : IFlyOptionsAccessor
{
    public StubFlyOptionsAccessor(FlyOptions options) => Current = options;
    public FlyOptions Current { get; }
}

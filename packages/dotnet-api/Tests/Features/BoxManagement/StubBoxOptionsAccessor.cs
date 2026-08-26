using Source.Features.BoxManagement.Configuration;

namespace Api.Tests.Features.BoxManagement;

/// <summary>
/// Minimal in-memory stand-in for <see cref="IBoxOptionsAccessor"/> in unit tests.
/// The SystemSettings round-trip is covered by its own tests, so Box-feature tests
/// just hand a pre-built <see cref="BoxOptions"/> here.
/// </summary>
public sealed class StubBoxOptionsAccessor : IBoxOptionsAccessor
{
    public StubBoxOptionsAccessor(BoxOptions options) => Current = options;
    public BoxOptions Current { get; }
}

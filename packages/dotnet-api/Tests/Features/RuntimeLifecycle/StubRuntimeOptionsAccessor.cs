using Source.Features.RuntimeLifecycle.Configuration;

namespace Api.Tests.Features.RuntimeLifecycle;

/// <summary>
/// Minimal in-memory stand-in for <see cref="IRuntimeOptionsAccessor"/> in unit tests.
/// Mirrors <c>StubFlyOptionsAccessor</c> — the SystemSettings round-trip is covered
/// by its own tests, so the lifecycle-job tests don't need a real DB.
/// </summary>
internal sealed class StubRuntimeOptionsAccessor : IRuntimeOptionsAccessor
{
    public StubRuntimeOptionsAccessor(RuntimeOptions options) => Current = options;
    public RuntimeOptions Current { get; }
}

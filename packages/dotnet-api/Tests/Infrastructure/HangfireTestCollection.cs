namespace Api.Tests.Infrastructure;

/// <summary>
/// xUnit test collection for all tests that touch Hangfire's static global state
/// (<c>GlobalJobFilters.Filters</c>, <c>JobStorage.Current</c>, <c>LibLog</c>'s cached
/// provider). xUnit runs all tests that share a <see cref="CollectionAttribute"/> name
/// sequentially, which is required here: <c>Hangfire.InMemory</c>'s dispatcher threads
/// and LibLog's logger-factory cache can be disposed mid-test when classes run in parallel,
/// yielding spurious <see cref="ObjectDisposedException"/>s.
///
/// <para>Attach <c>[Collection(HangfireTestCollection.Name)]</c> to any class that derives
/// from <see cref="HangfireTestBase"/> or uses <see cref="IntegrationTestBase"/> in a way
/// that depends on the host's Hangfire wiring.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class HangfireTestCollection
{
    public const string Name = "Hangfire";
}

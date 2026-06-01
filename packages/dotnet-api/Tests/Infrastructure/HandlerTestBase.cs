using Microsoft.Extensions.Logging;
using Moq;
using Source.Infrastructure;

namespace Api.Tests.Infrastructure;

/// <summary>
/// Base class for handler tests providing common test infrastructure
/// </summary>
public abstract class HandlerTestBase : IDisposable
{
    protected ApplicationDbContext Context { get; }

    protected HandlerTestBase()
    {
        Context = TestDbContextFactory.Create();
    }

    /// <summary>
    /// Creates a mock logger for the specified type
    /// </summary>
    protected static ILogger<T> CreateLogger<T>()
    {
        return new Mock<ILogger<T>>().Object;
    }

    public void Dispose()
    {
        Context.Dispose();
        GC.SuppressFinalize(this);
    }
}

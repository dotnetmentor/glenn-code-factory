using Hangfire;
using Hangfire.Common;
using Microsoft.Extensions.DependencyInjection;
using Source.Infrastructure.ErrorHandling;

namespace Api.Tests.Infrastructure.ErrorHandling;

/// <summary>
/// Verifies that the <see cref="ErrorCaptureJobFilter"/> is installed globally when the
/// application host starts. Without this, job failures bypass the error pipeline entirely.
///
/// <para>We spin up the full in-memory host via <see cref="IntegrationTestBase"/> — that host
/// runs with <c>Features:EnableHangfire=false</c>, so we additionally unit-check the DI graph
/// (i.e. confirm the filter is reachable) AND verify the global <see cref="GlobalJobFilters"/>
/// collection contains it once the DI registrations have been applied.</para>
/// </summary>
[Collection(HangfireTestCollection.Name)]
public class ErrorCaptureJobFilterRegistrationTests : IntegrationTestBase
{
    [Fact]
    public void GlobalJobFilters_ContainsErrorCaptureJobFilter_AfterHostStart()
    {
        // Triggering Services also triggers host build, which runs startup (which must
        // register the filter globally).
        _ = Services;

        GlobalJobFilters.Filters
            .Select(f => f.Instance)
            .OfType<ErrorCaptureJobFilter>()
            .Should()
            .NotBeEmpty("startup must register the ErrorCaptureJobFilter globally so every job pipeline sees it");
    }

    [Fact]
    public void ErrorCaptureJobFilter_IsResolvableFromDI()
    {
        using var scope = Services.CreateScope();
        var filter = scope.ServiceProvider.GetService<ErrorCaptureJobFilter>();
        filter.Should().NotBeNull("the filter should be registered in DI so it can be resolved for global registration");
    }
}

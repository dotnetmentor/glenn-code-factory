using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Source.Features.ErrorLog.Models;
using Source.Infrastructure;
using Source.Shared;

namespace Api.Tests.Infrastructure;

/// <summary>
/// Smoke tests that prove <see cref="IntegrationTestBase"/> wires up correctly:
///   - The HTTP pipeline responds.
///   - Per-test service overrides are honored.
///   - Each test instance gets an isolated in-memory database.
/// </summary>
[Collection(HangfireTestCollection.Name)]
public class IntegrationTestBaseExampleTests : IntegrationTestBase
{
    [Fact]
    public async Task Health_ReturnsOk_WithExpectedShape()
    {
        // /health is a public endpoint mapped in Program.cs — no auth required,
        // perfect smoke test for the WebApplicationFactory pipeline.
        var response = await Client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadFromJsonAsync<HealthResponse>();
        payload.Should().NotBeNull();
        payload!.Status.Should().Be("healthy");
    }

    [Fact]
    public void OverrideClock_PerTest_ServiceSeesOverriddenTime()
    {
        // Arrange: pick a distinctive fixed time that no real clock could produce right now.
        var fixedTime = new DateTime(2023, 6, 15, 9, 30, 0, DateTimeKind.Utc);
        WithService<IClock>(new FakeClock(fixedTime));

        // Act: resolve IClock from the test host's DI container.
        var clock = Services.GetRequiredService<IClock>();

        // Assert: we get the fake back, not the real SystemClock.
        clock.Should().BeOfType<FakeClock>();
        clock.UtcNow.Should().Be(fixedTime);
    }

    [Fact]
    public async Task TwoTestsInSameClass_SeeIsolatedDbState_PartA_Insert()
    {
        // Insert a sentinel row via the test host's DbContext. Because each test instance
        // gets its own factory (and therefore its own in-memory DB name), the row inserted
        // here must NOT be visible to TwoTestsInSameClass_SeeIsolatedDbState_PartB_Verify.
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.ErrorLogs.Add(new ErrorLog
        {
            Id = Guid.NewGuid(),
            Message = "sentinel-from-part-a",
            Source = "IntegrationTest",
            Severity = "Info",
            IsResolved = false,
        });
        await db.SaveChangesAsync();

        var count = await db.ErrorLogs.CountAsync(e => e.Message == "sentinel-from-part-a");
        count.Should().Be(1, "the row we just inserted should be visible in this test's DB");
    }

    [Fact]
    public async Task TwoTestsInSameClass_SeeIsolatedDbState_PartB_Verify()
    {
        // Fresh test instance => fresh in-memory DB. The row inserted in PartA must be absent.
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var count = await db.ErrorLogs.CountAsync(e => e.Message == "sentinel-from-part-a");
        count.Should().Be(0, "each test instance must get an isolated in-memory database");
    }

    private record HealthResponse(string Status, DateTime Timestamp);
}

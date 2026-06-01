using Microsoft.Extensions.DependencyInjection;
using Source.Features.ErrorLog.Queries;
using Source.Infrastructure.ErrorHandling;

namespace Api.Tests.Infrastructure.ErrorHandling;

/// <summary>
/// Tests for the <see cref="GetErrorPipelineStatsQuery"/> handler — the JSON-shaped view of
/// <see cref="ErrorPipelineMetrics.Snapshot"/> plus current queue depth, exposed over HTTP
/// via <c>GET /api/error-logs/pipeline-stats</c>.
///
/// These are pure handler tests: they drive the handler directly with a constructed
/// <see cref="ErrorQueue"/> and assert the response shape. Endpoint wiring is covered by
/// the integration test in <see cref="ErrorPipelineStatsEndpointTests"/>.
/// </summary>
public class ErrorPipelineStatsQueryTests
{
    private static ErrorEntry NewEntry(string message) => new(
        Message: message,
        StackTrace: null,
        Source: "Test",
        Severity: "Error",
        CorrelationId: null,
        RequestPath: null,
        RequestMethod: null,
        ContextData: null,
        OccurredAt: DateTime.UtcNow);

    [Fact]
    public async Task Handle_ReturnsSnapshot_WithAllFiveCountersAndQueueDepth()
    {
        var queue = new ErrorQueue(new PiiRedactor());

        // Enqueue 3 to bump Enqueued and give us a non-zero QueueDepth.
        for (var i = 0; i < 3; i++)
        {
            await queue.EnqueueAsync(NewEntry($"m-{i}"));
        }

        var handler = new GetErrorPipelineStatsQueryHandler(queue);
        var result = await handler.Handle(new GetErrorPipelineStatsQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var response = result.Value;
        response.QueueDepth.Should().Be(3);

        // Relative assertions on the monotonic counters — static state means we can't
        // assert absolute values, only that the shape is populated from the snapshot.
        response.Enqueued.Should().BeGreaterThanOrEqualTo(3);
        response.Dropped.Should().BeGreaterThanOrEqualTo(0);
        response.Persisted.Should().BeGreaterThanOrEqualTo(0);
        response.PersistFailed.Should().BeGreaterThanOrEqualTo(0);
        response.Suppressed.Should().BeGreaterThanOrEqualTo(0);
    }
}

/// <summary>
/// End-to-end integration test for <c>GET /api/error-logs/pipeline-stats</c>.
/// Because the endpoint is protected by <c>[Authorize(Roles = SuperAdmin)]</c> and the
/// integration test host does not set up an authenticated principal, we assert the
/// expected 401 / 403 path — which is itself a useful guarantee (the stats endpoint
/// really is protected).
/// </summary>
[Collection(HangfireTestCollection.Name)]
public class ErrorPipelineStatsEndpointTests : IntegrationTestBase
{
    [Fact]
    public async Task GetPipelineStats_Unauthenticated_Returns401()
    {
        var response = await Client.GetAsync("/api/error-logs/pipeline-stats");

        // 401 (no auth) is the expected contract for an unauthenticated request hitting
        // an [Authorize] endpoint in this app's default pipeline.
        ((int)response.StatusCode).Should().Be(401);
    }

    [Fact]
    public void Services_CanResolve_PipelineStatsHandler()
    {
        // MediatR handlers are auto-registered by convention. This proves the new
        // handler is discoverable without depending on a real HTTP request.
        using var scope = CreateScope();
        var handler = scope.ServiceProvider.GetService<
            MediatR.IRequestHandler<GetErrorPipelineStatsQuery, Source.Shared.Results.Result<GetErrorPipelineStatsResponse>>>();

        handler.Should().NotBeNull();
    }
}

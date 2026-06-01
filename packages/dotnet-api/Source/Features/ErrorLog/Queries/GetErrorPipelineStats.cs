using Source.Infrastructure.ErrorHandling;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.ErrorLog.Queries;

/// <summary>
/// JSON-shaped view of the five pipeline counters from <see cref="ErrorPipelineMetrics"/>
/// plus the current <see cref="ErrorQueue.ApproximateDepth"/>. Returned by
/// <c>GET /api/error-logs/pipeline-stats</c> for operators / dashboards that want structured
/// data rather than scraping the summary log line.
/// </summary>
public record GetErrorPipelineStatsQuery : IQuery<Result<GetErrorPipelineStatsResponse>>;

public record GetErrorPipelineStatsResponse
{
    /// <summary>Cumulative count of entries successfully written to the bounded channel.</summary>
    public required long Enqueued { get; init; }

    /// <summary>Cumulative count of entries evicted by DropOldest due to bounded capacity.</summary>
    public required long Dropped { get; init; }

    /// <summary>Cumulative count of rows persisted to the database.</summary>
    public required long Persisted { get; init; }

    /// <summary>Cumulative count of rows dropped because SaveChanges threw.</summary>
    public required long PersistFailed { get; init; }

    /// <summary>Cumulative count of detail rows suppressed by the per-signature sample cap.</summary>
    public required long Suppressed { get; init; }

    /// <summary>Current (approximate) number of entries sitting in the channel waiting to be flushed.</summary>
    public required int QueueDepth { get; init; }
}

public class GetErrorPipelineStatsQueryHandler : IQueryHandler<GetErrorPipelineStatsQuery, Result<GetErrorPipelineStatsResponse>>
{
    private readonly ErrorQueue _queue;

    public GetErrorPipelineStatsQueryHandler(ErrorQueue queue)
    {
        _queue = queue;
    }

    public Task<Result<GetErrorPipelineStatsResponse>> Handle(
        GetErrorPipelineStatsQuery request,
        CancellationToken cancellationToken)
    {
        var snap = ErrorPipelineMetrics.Snapshot();

        var response = new GetErrorPipelineStatsResponse
        {
            Enqueued = snap.Enqueued,
            Dropped = snap.Dropped,
            Persisted = snap.Persisted,
            PersistFailed = snap.PersistFailed,
            Suppressed = snap.Suppressed,
            QueueDepth = _queue.ApproximateDepth,
        };

        return Task.FromResult(Result.Success(response));
    }
}

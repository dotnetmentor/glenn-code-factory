using Source.Features.RuntimeWakeObservability.Internal;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimeWakeObservability.Queries.GetWakeMetricsSummary;

/// <summary>
/// Handler for <see cref="GetWakeMetricsSummaryQuery"/>. Resolves the wake
/// windows for the requested <c>(window, region)</c> pair via
/// <see cref="WakeWindowResolver"/>, then reduces them to p50 / p95 / count
/// using <see cref="Percentiles.NearestRank"/>.
///
/// <para><b>v1 vs v2.</b> Reads <c>RuntimeStateEvents</c> directly per the
/// runtime-wake-observability spec. <b>v2</b> will read pre-aggregated
/// p50 / p95 / count from a <c>RuntimeMetricsRollup</c> hourly bucket table once
/// fleet event volume makes per-request aggregation expensive.</para>
/// </summary>
public sealed class GetWakeMetricsSummaryHandler
    : IQueryHandler<GetWakeMetricsSummaryQuery, Result<WakeMetricsSummaryResponse>>
{
    private readonly WakeWindowResolver _resolver;

    public GetWakeMetricsSummaryHandler(WakeWindowResolver resolver)
    {
        _resolver = resolver;
    }

    public async Task<Result<WakeMetricsSummaryResponse>> Handle(
        GetWakeMetricsSummaryQuery request,
        CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;

        var span = WakeWindowSpan.TryParse(request.Window, nowUtc);
        if (span.IsFailure)
        {
            return Result.Failure<WakeMetricsSummaryResponse>(span.Error!);
        }

        var (start, end) = span.Value;

        var windows = await _resolver.ResolveAsync(start, end, request.Region, cancellationToken);

        if (windows.Count == 0)
        {
            // Empty-state contract: zero count, zero percentiles. The frontend
            // renders the empty header copy from this shape rather than from a
            // distinct error response.
            return Result.Success(new WakeMetricsSummaryResponse(
                P50Ms: 0,
                P95Ms: 0,
                Count: 0,
                AsOf: nowUtc));
        }

        var durations = new List<long>(windows.Count);
        foreach (var w in windows)
        {
            durations.Add(w.DurationMs);
        }

        var (p50, p95) = Percentiles.NearestRank(durations);

        return Result.Success(new WakeMetricsSummaryResponse(
            P50Ms: p50,
            P95Ms: p95,
            Count: windows.Count,
            AsOf: nowUtc));
    }
}

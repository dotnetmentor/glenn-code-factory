using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeEvents.Models;
using Source.Features.RuntimeWakeObservability.Internal;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimeWakeObservability.Queries.GetWakeStageBreakdown;

/// <summary>
/// Handler for <see cref="GetWakeStageBreakdownQuery"/>. Resolves the completed
/// wake windows for the requested <c>(window, region)</c> via
/// <see cref="WakeWindowResolver"/>, then pulls every
/// <see cref="RuntimeEventTypes.BootstrapStageCompleted"/> row whose
/// <c>(RuntimeId, Timestamp)</c> falls inside one of those windows. The rows
/// are grouped by the <c>stageName</c> value parsed from the <c>Payload</c>
/// jsonb and reduced to per-stage p50 / p95 / count.
///
/// <para><b>Why we re-pull and group in memory.</b> Postgres can group on
/// <c>(Payload-&gt;&gt;'stageName')</c> directly, but the EF translation is
/// fiddly and the row count per wake is tiny (a handful of stages each). One
/// IN-clause read + a HashMap reduce keeps the handler simple and avoids
/// jsonb-LINQ provider quirks across EF Core versions.</para>
///
/// <para><b>v1 vs v2.</b> Per-stage aggregates today are computed on read from
/// the <see cref="RuntimeEvent"/> ring buffer. v2 reads from a
/// <c>RuntimeMetricsRollup</c> table keyed on <c>(stage, region, hourBucket)</c>
/// with pre-aggregated p50 / p95 / count.</para>
/// </summary>
public sealed class GetWakeStageBreakdownHandler
    : IQueryHandler<GetWakeStageBreakdownQuery, Result<WakeStageBreakdownResponse>>
{
    private readonly WakeWindowResolver _resolver;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<GetWakeStageBreakdownHandler> _logger;

    public GetWakeStageBreakdownHandler(
        WakeWindowResolver resolver,
        ApplicationDbContext db,
        ILogger<GetWakeStageBreakdownHandler> logger)
    {
        _resolver = resolver;
        _db = db;
        _logger = logger;
    }

    public async Task<Result<WakeStageBreakdownResponse>> Handle(
        GetWakeStageBreakdownQuery request,
        CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;

        var span = WakeWindowSpan.TryParse(request.Window, nowUtc);
        if (span.IsFailure)
        {
            return Result.Failure<WakeStageBreakdownResponse>(span.Error!);
        }

        var (start, end) = span.Value;

        var windows = await _resolver.ResolveAsync(start, end, request.Region, cancellationToken);

        if (windows.Count == 0)
        {
            return Result.Success(new WakeStageBreakdownResponse(
                Stages: new List<WakeStageMetric>(),
                AsOf: nowUtc));
        }

        // Group windows by RuntimeId so we can do one Timestamp-range check per
        // event by looking up the runtime's wake window(s). A runtime can have
        // more than one wake in the request window — we keep them as a list and
        // include an event if its Timestamp falls in ANY of the runtime's
        // windows.
        var windowsByRuntime = new Dictionary<Guid, List<(DateTime Start, DateTime End)>>(windows.Count);
        foreach (var w in windows)
        {
            if (!windowsByRuntime.TryGetValue(w.RuntimeId, out var list))
            {
                list = new List<(DateTime, DateTime)>();
                windowsByRuntime[w.RuntimeId] = list;
            }
            list.Add((w.StartedAt, w.EndedAt));
        }

        var runtimeIds = windowsByRuntime.Keys.ToList();

        // Bound the Timestamp scan to the union of every wake window. The
        // outermost bounds are the min start and max end across all windows,
        // which is at most a 7d span (window) + the longest wake duration. The
        // composite index on (RuntimeId, Type, Timestamp DESC) carries this.
        var minStart = windows.Min(w => w.StartedAt);
        var maxEnd = windows.Max(w => w.EndedAt);

        var rawEvents = await _db.RuntimeEvents
            .AsNoTracking()
            .Where(e => e.Type == RuntimeEventTypes.BootstrapStageCompleted
                        && runtimeIds.Contains(e.RuntimeId)
                        && e.Timestamp >= minStart
                        && e.Timestamp <= maxEnd
                        && e.DurationMs != null)
            .Select(e => new
            {
                e.RuntimeId,
                e.Timestamp,
                e.DurationMs,
                e.Payload,
            })
            .ToListAsync(cancellationToken);

        // Group by stage name. Each stage gets its own duration list which we
        // reduce in one pass at the end.
        var byStage = new Dictionary<string, List<long>>(StringComparer.Ordinal);

        foreach (var ev in rawEvents)
        {
            // Belt-and-braces: events without a DurationMs are filtered in the
            // SQL above, but the value is still nullable on the CLR side.
            if (ev.DurationMs is not { } durationMs)
            {
                continue;
            }

            if (!windowsByRuntime.TryGetValue(ev.RuntimeId, out var runtimeWindows))
            {
                continue;
            }

            // Event must fall inside at least one of the runtime's wake windows.
            // We use inclusive bounds on both sides — the start row's CreatedAt
            // is the moment the wake began, and an event emitted in that same
            // millisecond legitimately belongs to the wake.
            var insideAnyWindow = false;
            foreach (var (winStart, winEnd) in runtimeWindows)
            {
                if (ev.Timestamp >= winStart && ev.Timestamp <= winEnd)
                {
                    insideAnyWindow = true;
                    break;
                }
            }

            if (!insideAnyWindow)
            {
                continue;
            }

            var stageName = TryReadStageName(ev.Payload);
            if (stageName is null)
            {
                continue;
            }

            if (!byStage.TryGetValue(stageName, out var durations))
            {
                durations = new List<long>();
                byStage[stageName] = durations;
            }
            durations.Add(durationMs);
        }

        var stages = new List<WakeStageMetric>(byStage.Count);
        foreach (var (stageName, durations) in byStage)
        {
            var (p50, p95) = Percentiles.NearestRank(durations);
            stages.Add(new WakeStageMetric(
                StageName: stageName,
                P50Ms: p50,
                P95Ms: p95,
                Count: durations.Count));
        }

        // Sort by p95 desc so the dominant stage renders first. Ties broken by
        // count desc for stability when two stages have the same p95.
        stages.Sort((a, b) =>
        {
            var byP95 = b.P95Ms.CompareTo(a.P95Ms);
            if (byP95 != 0) return byP95;
            var byCount = b.Count.CompareTo(a.Count);
            if (byCount != 0) return byCount;
            return string.Compare(a.StageName, b.StageName, StringComparison.Ordinal);
        });

        return Result.Success(new WakeStageBreakdownResponse(
            Stages: stages,
            AsOf: nowUtc));
    }

    /// <summary>
    /// Pull <c>payload.stageName</c> out of the jsonb payload. The daemon's
    /// <c>BootstrapStageCompleted</c> emitter always sets this — but we treat
    /// missing / malformed values as "skip" rather than failing the whole
    /// request: observability is best-effort and one bad row should not 500
    /// the dashboard.
    /// </summary>
    private string? TryReadStageName(string payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!doc.RootElement.TryGetProperty("stageName", out var stageNameEl))
            {
                return null;
            }

            if (stageNameEl.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var raw = stageNameEl.GetString();
            return string.IsNullOrWhiteSpace(raw) ? null : raw;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "GetWakeStageBreakdown: failed to parse BootstrapStageCompleted Payload as JSON; skipping row.");
            return null;
        }
    }
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeEvents.Models;
using Source.Features.RuntimeWakeObservability.Internal;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimeWakeObservability.Queries.GetSlowWakeSessions;

/// <summary>
/// Handler for <see cref="GetSlowWakeSessionsQuery"/>. Resolves the completed
/// wake windows via <see cref="WakeWindowResolver"/>, sorts them by duration
/// descending, takes the top <c>Limit</c>, and decorates each kept wake with
/// its dominant <c>BootstrapStageCompleted</c> stage (the stage with the
/// largest <c>DurationMs</c> emitted inside that wake's window).
///
/// <para><b>Single round-trip for dominant stages.</b> We issue one
/// <c>RuntimeId IN (...)</c> query for the kept wakes' RuntimeIds, then group
/// in memory by RuntimeId and pick the max-duration stage per runtime. No N+1.</para>
///
/// <para><b>v1 vs v2.</b> v1 reads <c>RuntimeStateEvents</c> +
/// <c>RuntimeEvents</c> directly. v2 will read pre-aggregated rows from a
/// <c>RuntimeMetricsRollup</c> hourly bucket table.</para>
/// </summary>
public sealed class GetSlowWakeSessionsHandler
    : IQueryHandler<GetSlowWakeSessionsQuery, Result<SlowWakeSessionsResponse>>
{
    private readonly WakeWindowResolver _resolver;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<GetSlowWakeSessionsHandler> _logger;

    public GetSlowWakeSessionsHandler(
        WakeWindowResolver resolver,
        ApplicationDbContext db,
        ILogger<GetSlowWakeSessionsHandler> logger)
    {
        _resolver = resolver;
        _db = db;
        _logger = logger;
    }

    public async Task<Result<SlowWakeSessionsResponse>> Handle(
        GetSlowWakeSessionsQuery request,
        CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;

        var span = WakeWindowSpan.TryParse(request.Window, nowUtc);
        if (span.IsFailure)
        {
            return Result.Failure<SlowWakeSessionsResponse>(span.Error!);
        }

        var (start, end) = span.Value;

        // Limit is clamped at the controller layer; defence-in-depth here so a
        // direct handler caller (e.g. a unit test) still gets a bounded result.
        var limit = Math.Clamp(request.Limit, 1, 100);

        var windows = await _resolver.ResolveAsync(start, end, request.Region, cancellationToken);

        if (windows.Count == 0)
        {
            return Result.Success(new SlowWakeSessionsResponse(
                Sessions: new List<SlowWakeSession>(),
                AsOf: nowUtc));
        }

        // Top-N slowest. ListSort is in-place; we don't need the rest of the
        // list afterwards.
        var sorted = windows.OrderByDescending(w => w.DurationMs).Take(limit).ToList();

        // Dominant-stage lookup — one query covering every kept RuntimeId, then
        // bucketise in memory and pick the max-duration stage per (RuntimeId,
        // wakeWindow).
        var keptRuntimeIds = sorted.Select(w => w.RuntimeId).Distinct().ToList();
        var minStart = sorted.Min(w => w.StartedAt);
        var maxEnd = sorted.Max(w => w.EndedAt);

        var stageEvents = await _db.RuntimeEvents
            .AsNoTracking()
            .Where(e => e.Type == RuntimeEventTypes.BootstrapStageCompleted
                        && keptRuntimeIds.Contains(e.RuntimeId)
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

        // Group events by runtime id for O(wake_count) lookup below.
        var eventsByRuntime = stageEvents
            .GroupBy(e => e.RuntimeId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var sessions = new List<SlowWakeSession>(sorted.Count);
        foreach (var w in sorted)
        {
            string? dominantStage = null;

            if (eventsByRuntime.TryGetValue(w.RuntimeId, out var runtimeEvents))
            {
                long bestDuration = -1;
                foreach (var ev in runtimeEvents)
                {
                    if (ev.DurationMs is not { } durationMs)
                    {
                        continue;
                    }

                    // Event Timestamp must fall inside this wake window.
                    // A runtime can have multiple wakes in the request span —
                    // we only attribute events that landed inside *this* wake's
                    // (startedAt, endedAt) bracket.
                    if (ev.Timestamp < w.StartedAt || ev.Timestamp > w.EndedAt)
                    {
                        continue;
                    }

                    if (durationMs <= bestDuration)
                    {
                        continue;
                    }

                    var stageName = TryReadStageName(ev.Payload);
                    if (stageName is null)
                    {
                        continue;
                    }

                    bestDuration = durationMs;
                    dominantStage = stageName;
                }
            }

            sessions.Add(new SlowWakeSession(
                RuntimeId: w.RuntimeId,
                ProjectId: w.ProjectId,
                BranchId: w.BranchId,
                Region: w.Region,
                StartedAt: w.StartedAt,
                DurationMs: w.DurationMs,
                DominantStageName: dominantStage));
        }

        return Result.Success(new SlowWakeSessionsResponse(
            Sessions: sessions,
            AsOf: nowUtc));
    }

    /// <summary>
    /// Same shape as the breakdown handler's helper — parse the jsonb payload,
    /// skip malformed rows rather than failing the request.
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
                "GetSlowWakeSessions: failed to parse BootstrapStageCompleted Payload as JSON; skipping row.");
            return null;
        }
    }
}

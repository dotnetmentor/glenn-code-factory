using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeEvents.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimeEvents.Queries;

/// <summary>
/// Reverse-chronological page of <see cref="RuntimeEvent"/> rows for a single
/// runtime, with optional cursor (<paramref name="Before"/>) + type / severity
/// filters. Backs the user-facing
/// <see cref="Controllers.RuntimeEventsController.List"/> endpoint that powers
/// the runtime drawer's Timeline tab.
///
/// <para><b>Pagination.</b> Cursor-based on <see cref="RuntimeEvent.Timestamp"/>
/// (strictly less-than). Cursor pagination, not offset, because the event
/// store has a per-runtime rolling cap — offset would shift under us as old
/// events are evicted. The handler also takes <c>limit + 1</c> rows so it can
/// truthfully report <c>HasMore</c> without a second count round-trip.</para>
///
/// <para><b>Limit bounds.</b> <see cref="Limit"/> arrives from the
/// controller pre-clamped to [1, 500] (default 200 set at the controller
/// level); the handler treats whatever it gets as authoritative so a unit
/// test calling the handler directly with a wild value still gets sane
/// behaviour — it clamps once more here for belt-and-braces.</para>
///
/// <para><b>Index coverage.</b> The unfiltered Timeline scroll hits the
/// <c>IX_RuntimeEvents_RuntimeId_Timestamp</c> composite; the typed filter
/// hits <c>IX_RuntimeEvents_RuntimeId_Type_Timestamp</c>. Severity filtering
/// has no dedicated index (it's a low-cardinality column) and rides on the
/// runtime-bounded scan — fine because per-runtime row counts are capped at
/// 5000.</para>
/// </summary>
public record GetRuntimeEventsQuery(
    Guid RuntimeId,
    int Limit = 200,
    DateTime? Before = null,
    string? Type = null,
    RuntimeEventSeverity? Severity = null) : IQuery<Result<ListRuntimeEventsResponse>>;

/// <summary>
/// Wire shape for a single <see cref="RuntimeEvent"/> row served by the REST
/// endpoint. The <see cref="Payload"/> is exposed as a <see cref="JsonElement"/>
/// so the Swagger schema documents it as <c>object</c> and the frontend
/// receives parsed JSON — not a string the React side would have to
/// re-parse.
/// </summary>
public record RuntimeEventDto(
    Guid Id,
    Guid RuntimeId,
    string Type,
    string Severity,
    DateTime Timestamp,
    long? DurationMs,
    JsonElement Payload);

/// <summary>
/// Response envelope: the page of events + a <see cref="HasMore"/> flag
/// telling the frontend whether another <c>before=&lt;oldestTimestamp&gt;</c>
/// request would yield more rows. The flag is computed via the "fetch
/// limit+1, return limit, look at whether we saw the extra row" trick so we
/// don't need a separate <c>COUNT(*)</c> query.
/// </summary>
public record ListRuntimeEventsResponse(
    List<RuntimeEventDto> Events,
    bool HasMore);

public class GetRuntimeEventsQueryHandler
    : IQueryHandler<GetRuntimeEventsQuery, Result<ListRuntimeEventsResponse>>
{
    /// <summary>
    /// Hard cap on the page size — matches the controller's documented
    /// <c>max=500</c>. The runtime drawer never needs more than a few hundred
    /// rows on screen at once; a wild caller still gets a bounded response.
    /// </summary>
    public const int MAX_LIMIT = 500;

    /// <summary>
    /// Floor on the page size — prevents <c>limit=0</c> / negative-limit
    /// requests from returning an empty result that looks like "no events"
    /// when the runtime is actually busy. The controller defaults to 200 but
    /// a hand-crafted curl could still pass nonsense.
    /// </summary>
    public const int MIN_LIMIT = 1;

    private readonly ApplicationDbContext _db;
    private readonly ILogger<GetRuntimeEventsQueryHandler> _logger;

    public GetRuntimeEventsQueryHandler(
        ApplicationDbContext db,
        ILogger<GetRuntimeEventsQueryHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Result<ListRuntimeEventsResponse>> Handle(
        GetRuntimeEventsQuery request,
        CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(request.Limit, MIN_LIMIT, MAX_LIMIT);

        var q = _db.RuntimeEvents
            .AsNoTracking()
            .Where(e => e.RuntimeId == request.RuntimeId);

        if (request.Before is { } before)
        {
            // Strictly less-than: the cursor is the oldest timestamp the
            // caller has, so the next page starts at the event just before
            // it. Equal-timestamp ties are tolerated — the drawer doesn't
            // promise stable ordering at the millisecond boundary.
            q = q.Where(e => e.Timestamp < before);
        }

        if (!string.IsNullOrEmpty(request.Type))
        {
            q = q.Where(e => e.Type == request.Type);
        }

        if (request.Severity is { } sev)
        {
            q = q.Where(e => e.Severity == sev);
        }

        // Fetch limit + 1 so we can report HasMore without a second count
        // round-trip. If we get back limit+1 rows, there's at least one more
        // page; we drop the extra row before returning.
        var rows = await q
            .OrderByDescending(e => e.Timestamp)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > limit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var dtos = rows
            .Select(e => new RuntimeEventDto(
                Id: e.Id,
                RuntimeId: e.RuntimeId,
                Type: e.Type,
                Severity: e.Severity.ToString(),
                Timestamp: e.Timestamp,
                DurationMs: e.DurationMs,
                Payload: ParsePayloadOrEmpty(e.Payload)))
            .ToList();

        return Result.Success(new ListRuntimeEventsResponse(dtos, hasMore));
    }

    /// <summary>
    /// The persisted <see cref="RuntimeEvent.Payload"/> column is a jsonb
    /// string — parse it into a <see cref="JsonElement"/> so the wire shape
    /// surfaces as a structured object (Swagger generates <c>object</c>, the
    /// frontend reads JSON directly). On malformed JSON we log + return
    /// <c>{}</c> rather than failing the whole list: the Timeline is
    /// observability, and one corrupt row should not 500 the page.
    /// </summary>
    private JsonElement ParsePayloadOrEmpty(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "GetRuntimeEvents: failed to parse Payload column as JSON; returning empty object placeholder.");
            using var fallback = JsonDocument.Parse("{}");
            return fallback.RootElement.Clone();
        }
    }
}

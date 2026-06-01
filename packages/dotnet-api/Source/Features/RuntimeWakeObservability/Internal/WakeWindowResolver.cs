using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;

namespace Source.Features.RuntimeWakeObservability.Internal;

/// <summary>
/// Shared building block for the three RuntimeWakeObservability query handlers
/// — resolves "completed wake windows" out of the <see cref="RuntimeStateEvent"/>
/// audit log so each handler doesn't re-implement the join-by-RuntimeId-and-next-Online
/// dance.
///
/// <para>A <b>wake window</b> for a single <see cref="WakeWindow.RuntimeId"/> is the
/// CreatedAt interval bracketed by two audit rows:</para>
/// <list type="number">
///   <item><b>start</b>: the row with <c>FromState=Suspended, ToState=Waking</c> that
///         falls inside the caller's <c>(windowStart, windowEnd]</c> filter;</item>
///   <item><b>end</b>: the next-later row for the same RuntimeId with
///         <c>ToState=Online</c>.</item>
/// </list>
///
/// <para>If no Online row exists later than a given Waking row, the wake is
/// <b>incomplete</b> and the window is silently dropped — we only report on
/// completed wakes (matches every endpoint's "completed wakes" contract).</para>
///
/// <para><b>v1 implementation note.</b> This reads <c>RuntimeStateEvents</c> and
/// <c>ProjectRuntimes</c> directly per the runtime-wake-observability spec.
/// <b>v2</b> will read from a <c>RuntimeMetricsRollup</c> hourly bucket table
/// (count / p50 / p95 per stage per region) once read-time aggregation becomes a
/// real cost — see the spec's "Design Rationale" section. Until then the 5000-event
/// ring buffer per runtime plus the <c>(RuntimeId, CreatedAt)</c> composite on
/// <c>RuntimeStateEvents</c> keep this cheap enough for triage cadence.</para>
///
/// <para><b>Efficiency.</b> One projection query on <c>RuntimeStateEvents</c>
/// pulling only the columns we need, plus one optional join on
/// <c>ProjectRuntimes</c> when <paramref name="region"/> filtering is requested.
/// The pair-up is done in memory (sorted by RuntimeId + CreatedAt, single linear
/// pass) — Postgres window functions would be slicker but EF Core 9's translation
/// for <c>LAG</c>/<c>LEAD</c> is awkward and the row counts here are bounded by
/// the audit table's typical-day volume.</para>
/// </summary>
public sealed class WakeWindowResolver
{
    private readonly ApplicationDbContext _db;

    public WakeWindowResolver(ApplicationDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Enumerate completed wake windows whose <b>start</b> row was written in
    /// <c>(windowStart, windowEnd]</c>.
    ///
    /// <para>The end row is allowed to be later than <paramref name="windowEnd"/> —
    /// a wake that <i>started</i> inside the window but completed slightly after
    /// is still counted. Wakes whose end row never lands are dropped.</para>
    /// </summary>
    /// <param name="windowStart">UTC lower bound on the wake-start row's <c>CreatedAt</c> (exclusive).</param>
    /// <param name="windowEnd">UTC upper bound on the wake-start row's <c>CreatedAt</c> (inclusive).</param>
    /// <param name="region">Optional exact-match on <see cref="ProjectRuntime.Region"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Read-only list of completed wakes, each carrying the timestamps + ids the
    /// three observability handlers need to look up per-stage events and render
    /// rows. Order is unspecified — callers sort as needed.
    /// </returns>
    public async Task<IReadOnlyList<WakeWindow>> ResolveAsync(
        DateTime windowStart,
        DateTime windowEnd,
        string? region,
        CancellationToken ct)
    {
        // 1. Resolve the set of RuntimeIds we'll consider. When region filtering
        //    is requested we narrow up-front via a join on ProjectRuntimes so the
        //    RuntimeStateEvents scan stays bounded. Without a region filter we
        //    leave the runtime set open — every Suspended->Waking row in the
        //    window is a candidate.
        HashSet<Guid>? scopedRuntimeIds = null;
        if (!string.IsNullOrWhiteSpace(region))
        {
            scopedRuntimeIds = (await _db.ProjectRuntimes
                .AsNoTracking()
                .Where(r => r.Region == region)
                .Select(r => r.Id)
                .ToListAsync(ct))
                .ToHashSet();

            // Empty region filter -> no possible wakes. Short-circuit so the
            // RuntimeStateEvents query doesn't pull rows we'd just discard.
            if (scopedRuntimeIds.Count == 0)
            {
                return Array.Empty<WakeWindow>();
            }
        }

        // 2. Pull every Waking-start and Online-end row that could participate
        //    in a window. We need Online rows whose CreatedAt may be slightly
        //    after windowEnd (a wake that started in-window can complete just
        //    after), so we don't upper-bound the Online side.
        //
        //    We can't trivially upper-bound the Online side either, because we
        //    don't know how long completion takes. In practice the wake path
        //    is bounded to ~minutes, so reading the full Online stream from
        //    `windowStart` onward is the conservative-but-cheap shape — the
        //    (RuntimeId, CreatedAt) composite carries the filter.
        var eventsQuery = _db.RuntimeStateEvents
            .AsNoTracking()
            .Where(e => e.CreatedAt > windowStart
                        && (
                            (e.FromState == RuntimeState.Suspended && e.ToState == RuntimeState.Waking)
                            || e.ToState == RuntimeState.Online));

        if (scopedRuntimeIds is not null)
        {
            // EF Core 9 translates Contains-on-HashSet to a parameterised IN list.
            eventsQuery = eventsQuery.Where(e => scopedRuntimeIds.Contains(e.RuntimeId));
        }

        var rows = await eventsQuery
            .Select(e => new StateEventRow(
                e.RuntimeId,
                e.FromState,
                e.ToState,
                e.CreatedAt))
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            return Array.Empty<WakeWindow>();
        }

        // 3. Pair every Waking-start (inside the windowStart..windowEnd window)
        //    with the next-later Online row for the same runtime. Sort by
        //    (RuntimeId, CreatedAt) so we can walk the list linearly and pick
        //    the first Online that appears after a given Waking.
        rows.Sort((a, b) =>
        {
            var byRuntime = a.RuntimeId.CompareTo(b.RuntimeId);
            return byRuntime != 0 ? byRuntime : a.CreatedAt.CompareTo(b.CreatedAt);
        });

        // 4. Pull the ProjectId / BranchId / Region for every runtime that has at
        //    least one row. The three observability endpoints need these for the
        //    SlowSessions payload; pulling them eagerly here keeps the
        //    common-path single-trip even though Summary doesn't consume them.
        //    Soft-deleted ProjectRuntimes are excluded by the global query
        //    filter — wakes whose owning runtime has since been hard- or
        //    soft-deleted simply lose their metadata, which is acceptable for
        //    triage.
        var runtimeIdsWithRows = rows.Select(r => r.RuntimeId).Distinct().ToList();
        var runtimeMetadata = await _db.ProjectRuntimes
            .AsNoTracking()
            .Where(r => runtimeIdsWithRows.Contains(r.Id))
            .Select(r => new
            {
                r.Id,
                r.ProjectId,
                r.BranchId,
                r.Region,
            })
            .ToDictionaryAsync(r => r.Id, ct);

        var windows = new List<WakeWindow>();

        // Linear pass: for each Waking-start, scan forward in the same
        // runtime's slice until we find the first Online row strictly after it.
        // The list is grouped by RuntimeId because of the sort above, so we can
        // bail the inner search as soon as RuntimeId changes.
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];

            if (row.FromState != RuntimeState.Suspended || row.ToState != RuntimeState.Waking)
            {
                continue;
            }

            // Only Waking-starts that landed in (windowStart, windowEnd] qualify.
            if (row.CreatedAt > windowEnd)
            {
                continue;
            }

            DateTime? endAt = null;
            for (var j = i + 1; j < rows.Count; j++)
            {
                var candidate = rows[j];
                if (candidate.RuntimeId != row.RuntimeId)
                {
                    break;
                }

                if (candidate.ToState == RuntimeState.Online && candidate.CreatedAt > row.CreatedAt)
                {
                    endAt = candidate.CreatedAt;
                    break;
                }
            }

            if (endAt is null)
            {
                // No Online row landed later than this Waking-start — the wake
                // is still in flight (or failed before reaching Online). Drop
                // per spec.
                continue;
            }

            var durationMs = (long)Math.Round((endAt.Value - row.CreatedAt).TotalMilliseconds);

            // Metadata may be missing if the runtime has been hard-deleted since
            // the audit row was written. Surface what we have; SlowSessions
            // callers fall back to empties.
            runtimeMetadata.TryGetValue(row.RuntimeId, out var meta);

            windows.Add(new WakeWindow(
                RuntimeId: row.RuntimeId,
                ProjectId: meta?.ProjectId ?? Guid.Empty,
                BranchId: meta?.BranchId ?? Guid.Empty,
                Region: meta?.Region ?? string.Empty,
                StartedAt: row.CreatedAt,
                EndedAt: endAt.Value,
                DurationMs: durationMs));
        }

        return windows;
    }

    /// <summary>
    /// Tight projection of a single <see cref="RuntimeStateEvent"/> row — only
    /// the fields the pair-up algorithm needs. Avoids materialising the full
    /// entity (with Metadata / Reason / TriggeredBy) into memory.
    /// </summary>
    private sealed record StateEventRow(
        Guid RuntimeId,
        RuntimeState? FromState,
        RuntimeState ToState,
        DateTime CreatedAt);
}

/// <summary>
/// A single completed wake — the start row in <c>RuntimeStateEvents</c>
/// (<c>Suspended -&gt; Waking</c>) paired with the next <c>* -&gt; Online</c>
/// row for the same runtime.
/// </summary>
/// <param name="RuntimeId">Runtime that woke up.</param>
/// <param name="ProjectId">Owning project; <see cref="Guid.Empty"/> when the runtime row has been hard-deleted.</param>
/// <param name="BranchId">Branch the runtime is pinned to; <see cref="Guid.Empty"/> when missing.</param>
/// <param name="Region">Fly region the runtime lives in; empty string when missing.</param>
/// <param name="StartedAt">UTC <c>CreatedAt</c> of the <c>Suspended -&gt; Waking</c> audit row.</param>
/// <param name="EndedAt">UTC <c>CreatedAt</c> of the matching <c>* -&gt; Online</c> audit row.</param>
/// <param name="DurationMs">Milliseconds between <paramref name="StartedAt"/> and <paramref name="EndedAt"/>.</param>
public sealed record WakeWindow(
    Guid RuntimeId,
    Guid ProjectId,
    Guid BranchId,
    string Region,
    DateTime StartedAt,
    DateTime EndedAt,
    long DurationMs);

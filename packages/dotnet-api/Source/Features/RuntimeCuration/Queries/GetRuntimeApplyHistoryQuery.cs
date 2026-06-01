using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeCuration.Models;
using Source.Features.RuntimeEvents.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;
using Tapper;

namespace Source.Features.RuntimeCuration.Queries;

/// <summary>
/// Decided-proposal history (Applied / Failed) for a (project, branch) pair.
/// Powers audit item A7 — the Spec tab's "Apply history" section in the
/// super-admin runtime drawer. The drawer reads the last N proposals that
/// reached a terminal apply outcome, with their per-phase timings inlined so
/// the operator can spot "why did this apply take 47s? — it was setup".
///
/// <list type="bullet">
///   <item>Scope is <c>(ProjectId, BranchId)</c> — the drawer is opened from a
///         branch tab, so we never want sibling-branch proposals leaking into
///         this surface.</item>
///   <item>Status filter is intentionally fixed to terminal-decided states
///         (<see cref="RuntimeProposalStatus.Applied"/> and
///         <see cref="RuntimeProposalStatus.Failed"/>) — the spec says
///         <c>?status=Applied,Failed</c> and we hard-code that here so callers
///         can't accidentally fetch Pending or Rejected rows that this surface
///         is not designed for. Bumping the filter is a one-line change.</item>
///   <item><see cref="Limit"/> defaults to 20 (per the spec); clamped to
///         <c>[1, 100]</c>. The drawer never asks for more.</item>
///   <item>Per-row <see cref="RuntimeApplyHistoryItem.TotalApplyMs"/> and
///         <see cref="RuntimeApplyHistoryItem.PhaseTimings"/> are pulled
///         <b>first</b> from the persisted <see cref="RuntimeProposal.TotalApplyMs"/>
///         / <see cref="RuntimeProposal.PhaseTimings"/> columns (stamped by
///         <c>RecordApplyResultCommandHandler</c> the moment the daemon
///         acks), and only fall back to the matching <c>SpecDeltaApplied</c> /
///         <c>SpecDeltaFailed</c> <see cref="RuntimeEvent"/> payload when the
///         persisted columns are still null (older rows that pre-date the
///         <c>AddRuntimeProposalTimingColumns</c> migration). Both can be
///         null in the worst case (very old row whose event also got evicted
///         by the rolling cap).</item>
/// </list>
///
/// <para>Returns an empty list when the runtime doesn't exist (or the project
/// has no runtime on that branch) — the controller surfaces 200 + [] rather
/// than 404 so the drawer renders "no apply history yet" cleanly.</para>
/// </summary>
public record GetRuntimeApplyHistoryQuery(
    Guid ProjectId,
    Guid BranchId,
    int Limit) : IQuery<Result<List<RuntimeApplyHistoryItem>>>;

/// <summary>
/// One row in the apply-history table. <see cref="ProposalId"/> short-prefixes
/// to a render-able badge; <see cref="DecidedBy"/> is the IdentityUser id of
/// the operator who hit Approve / Edit. <see cref="TotalApplyMs"/> +
/// <see cref="PhaseTimings"/> ride as nullable because the matching
/// <c>SpecDelta*</c> event may have been dropped by the rolling FIFO cap
/// (5000 events per runtime) before the drawer opens.
/// </summary>
[TranspilationSource]
public record RuntimeApplyHistoryItem
{
    /// <summary>Proposal row id — the drawer shortens this for display.</summary>
    public required Guid ProposalId { get; init; }

    /// <summary>
    /// IdentityUser id that decided the proposal (Approve / Edit). Null for
    /// rare states where the proposal was auto-decided by a non-user actor —
    /// not reachable today but kept nullable for forward compatibility.
    /// </summary>
    public string? DecidedBy { get; init; }

    /// <summary>UTC timestamp the proposal was decided.</summary>
    public DateTime? DecidedAt { get; init; }

    /// <summary>
    /// Terminal status — either <see cref="RuntimeProposalStatus.Applied"/>
    /// or <see cref="RuntimeProposalStatus.Failed"/> (the query filters to
    /// these two only).
    /// </summary>
    public required RuntimeProposalStatus Status { get; init; }

    /// <summary>
    /// Total apply duration in milliseconds, sourced from the matching
    /// <c>SpecDeltaApplied</c> / <c>SpecDeltaFailed</c> event's
    /// <see cref="RuntimeEvent.DurationMs"/>. Null when the event isn't
    /// available (rolling cap may have dropped it).
    /// </summary>
    public long? TotalApplyMs { get; init; }

    /// <summary>
    /// Per-phase timing breakdown as a JSON string — typically
    /// <c>{ installMs, servicesMs, setupMs }</c>. Sourced from the matching
    /// <c>SpecDelta*</c> event's payload. Null when the event row is missing
    /// or doesn't carry phase data. Frontend parses on render and falls back
    /// to "no phase breakdown" when null.
    /// </summary>
    public string? PhaseTimings { get; init; }

    /// <summary>
    /// On <see cref="RuntimeProposalStatus.Failed"/>, the daemon's error
    /// message stored on the proposal row. Null for successful applies.
    /// </summary>
    public string? ErrorMessage { get; init; }
}

public sealed class GetRuntimeApplyHistoryHandler
    : IQueryHandler<GetRuntimeApplyHistoryQuery, Result<List<RuntimeApplyHistoryItem>>>
{
    /// <summary>
    /// Hard cap on history depth — anything larger would page past the index
    /// window and the drawer never needs more than a screenful anyway.
    /// </summary>
    private const int MaxLimit = 100;

    private readonly ApplicationDbContext _db;

    public GetRuntimeApplyHistoryHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<List<RuntimeApplyHistoryItem>>> Handle(
        GetRuntimeApplyHistoryQuery request,
        CancellationToken cancellationToken)
    {
        // Resolve the runtime for (project, branch). The drawer is always
        // branch-scoped — if we filtered by ProjectId alone we'd surface
        // sibling-branch proposals in a tab that's reading "the runtime I'm
        // looking at right now". A project + branch has one ProjectRuntime
        // (most-recent, non-deleted) by the same convention as RuntimeStatusController.
        var runtime = await _db.ProjectRuntimes
            .AsNoTracking()
            .Where(r => r.ProjectId == request.ProjectId && r.BranchId == request.BranchId)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new { r.Id })
            .FirstOrDefaultAsync(cancellationToken);

        if (runtime is null)
        {
            // No runtime → no history. Empty list (not failure) so the drawer
            // renders "no apply history yet" without an error toast.
            return Result.Success(new List<RuntimeApplyHistoryItem>());
        }

        var limit = Math.Clamp(request.Limit, 1, MaxLimit);

        // Pull the decided proposals first (small index-friendly read), then
        // hydrate the matching event rows in a second query. Two reads beat a
        // one-shot join because RuntimeEvent.RuntimeId has no FK to
        // RuntimeProposal — the events are append-only with deliberate
        // schema independence (see RuntimeEvent.cs).
        var proposals = await _db.RuntimeProposals
            .AsNoTracking()
            .Where(p => p.RuntimeId == runtime.Id
                     && (p.Status == RuntimeProposalStatus.Applied
                      || p.Status == RuntimeProposalStatus.Failed))
            .OrderByDescending(p => p.DecidedAt)
            .Take(limit)
            .Select(p => new
            {
                p.Id,
                p.Status,
                p.DecidedBy,
                p.DecidedAt,
                p.ErrorMessage,
                p.TotalApplyMs,
                p.PhaseTimings,
            })
            .ToListAsync(cancellationToken);

        if (proposals.Count == 0)
        {
            return Result.Success(new List<RuntimeApplyHistoryItem>());
        }

        // Hydrate timings from the matching SpecDelta* events. We search by
        // RuntimeId + Type and join client-side via the proposalId embedded in
        // the payload jsonb. The alternative — a SQL jsonb predicate — would
        // be more elegant but breaks the EF InMemory provider that tests use,
        // and the proposals page is small (≤100 rows) so the client join is
        // O(n*m) on bounded n,m.
        //
        // We pull a moderate window of recent SpecDelta* events so even a
        // busy runtime's latest applies are covered. The 5000-cap on
        // RuntimeEvent means we can't go arbitrarily back, but for "last 20
        // applied proposals" this is comfortably enough.
        var oldestDecidedAt = proposals.Min(p => p.DecidedAt) ?? DateTime.UtcNow.AddDays(-30);
        // Subtract a small margin — the SpecDelta event timestamp is the
        // moment the daemon emitted the result, which lands a few ms before
        // the proposal row is updated. A 60-second window is forgiving.
        var sinceCutoff = oldestDecidedAt.AddSeconds(-60);

        var candidateEvents = await _db.RuntimeEvents
            .AsNoTracking()
            .Where(e => e.RuntimeId == runtime.Id
                     && e.Timestamp >= sinceCutoff
                     && (e.Type == RuntimeEventTypes.SpecDeltaApplied
                      || e.Type == RuntimeEventTypes.SpecDeltaFailed))
            .Select(e => new
            {
                e.Type,
                e.Timestamp,
                e.DurationMs,
                e.Payload,
            })
            .ToListAsync(cancellationToken);

        // Index candidates by proposalId extracted from the payload. The
        // payload shape is daemon-owned freeform jsonb so we treat the
        // proposalId field defensively (case-insensitive Json node lookup,
        // missing field tolerated).
        var eventsByProposalId = new Dictionary<Guid, (long? DurationMs, string Payload)>();
        foreach (var ev in candidateEvents)
        {
            var proposalId = TryExtractProposalId(ev.Payload);
            if (proposalId.HasValue && !eventsByProposalId.ContainsKey(proposalId.Value))
            {
                // First (most recent — events came back unordered, but
                // duplicates within a 60s window for the same proposalId are
                // a re-delivery so we prefer the first one we see).
                eventsByProposalId[proposalId.Value] = (ev.DurationMs, ev.Payload);
            }
        }

        var items = proposals
            .Select(p =>
            {
                eventsByProposalId.TryGetValue(p.Id, out var ev);
                return new RuntimeApplyHistoryItem
                {
                    ProposalId = p.Id,
                    DecidedBy = p.DecidedBy,
                    DecidedAt = p.DecidedAt,
                    Status = p.Status,
                    // Prefer the persisted columns (stamped by
                    // RecordApplyResultCommandHandler the moment the daemon
                    // acked) so the surface survives the rolling 5000-event
                    // cap on RuntimeEvent. Fall back to the event payload
                    // only for rows that pre-date the
                    // AddRuntimeProposalTimingColumns migration.
                    TotalApplyMs = p.TotalApplyMs ?? ev.DurationMs,
                    PhaseTimings = p.PhaseTimings ?? TryExtractPhaseTimings(ev.Payload),
                    ErrorMessage = p.ErrorMessage,
                };
            })
            .ToList();

        return Result.Success(items);
    }

    /// <summary>
    /// Best-effort proposalId extraction from a <see cref="RuntimeEvent.Payload"/>
    /// jsonb string. Returns <c>null</c> when the field is missing or unparseable —
    /// the caller treats null as "no matching event, surface nullable timings".
    /// </summary>
    private static Guid? TryExtractProposalId(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return null;
            }

            // The daemon emits `proposalId` in camelCase; tolerate PascalCase
            // for cross-version safety.
            if (doc.RootElement.TryGetProperty("proposalId", out var p1)
                || doc.RootElement.TryGetProperty("ProposalId", out p1))
            {
                if (p1.ValueKind == System.Text.Json.JsonValueKind.String
                    && Guid.TryParse(p1.GetString(), out var guid))
                {
                    return guid;
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Malformed payload — defensive observability table; skip.
        }

        return null;
    }

    /// <summary>
    /// Returns the <c>phaseTimings</c> sub-object as a raw JSON string,
    /// or <c>null</c> when the payload doesn't carry it. The frontend
    /// renders the breakdown ("install 1.2s · services 3.1s · setup 0.4s"),
    /// so we ship the structured shape verbatim rather than fan out into
    /// individual columns.
    /// </summary>
    private static string? TryExtractPhaseTimings(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return null;
            }

            if (doc.RootElement.TryGetProperty("phaseTimings", out var pt)
                || doc.RootElement.TryGetProperty("PhaseTimings", out pt))
            {
                return pt.GetRawText();
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Same defensive treatment as the proposalId extractor.
        }

        return null;
    }
}

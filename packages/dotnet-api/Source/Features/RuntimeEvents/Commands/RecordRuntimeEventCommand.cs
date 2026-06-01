using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeEvents.Models;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;
using Source.Shared;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimeEvents.Commands;

/// <summary>
/// Persists a single <see cref="RuntimeEvent"/> row and enforces the per-runtime
/// rolling FIFO cap. Consumed by:
/// <list type="bullet">
///   <item>the daemon-facing SignalR hub method (Card P3.2) — the hub projects
///         its connection's <c>rt_runtime</c> claim into <see cref="RuntimeId"/>
///         and dispatches this command;</item>
///   <item>the in-process emitters added in follow-up cards (e.g. when the
///         backend itself observes a spec validation failure before the spec
///         ever reaches the daemon — <c>SpecValidationFailed</c> originates
///         here, not on the daemon).</item>
/// </list>
///
/// <para><b>Best-effort.</b> Event persistence is observability, not load-bearing.
/// The handler logs and returns <see cref="Result.Failure(string)"/> on
/// exceptions rather than letting them bubble — a broken event store must
/// not break a working runtime. Callers should ignore the failure or log it,
/// but never abort their own operation because of it.</para>
///
/// <para><b>Rolling cap.</b> After every insert, if the runtime's event count
/// exceeds <see cref="MAX_EVENTS_PER_RUNTIME"/> (5000), the oldest excess is
/// deleted in one batch. Implemented via <c>ExecuteDeleteAsync</c> against a
/// LINQ subquery so nothing is round-tripped through the application — works
/// on Postgres in production and on the InMemory provider in tests (the
/// provider supports the LINQ form even though it doesn't translate raw
/// SQL).</para>
/// </summary>
public record RecordRuntimeEventCommand(
    Guid RuntimeId,
    string Type,
    RuntimeEventSeverity Severity,
    DateTime Timestamp,
    long? DurationMs,
    string Payload) : ICommand<Result>;

public class RecordRuntimeEventCommandHandler
    : ICommandHandler<RecordRuntimeEventCommand, Result>
{
    /// <summary>
    /// Per-runtime rolling FIFO cap. Past this point the oldest events are
    /// dropped on every insert. Picked to keep ~a day of busy traffic in the
    /// Timeline without unbounded growth — tunable here without a migration
    /// since the cap is enforced in code, not in a constraint.
    /// </summary>
    private const int MAX_EVENTS_PER_RUNTIME = 5000;

    /// <summary>
    /// Hard cap on a single event's payload size in bytes (UTF-8). Picked to
    /// be generous for normal observability shapes (typical payloads are
    /// 100–500 bytes; an SpecApplyAckFailed with a transcript can reach low
    /// kilobytes) while keeping a single bad emitter — e.g. a daemon
    /// accidentally shipping a megabyte of stderr — from blowing through the
    /// 5000-row cap budget in a few minutes and burying everything else.
    /// </summary>
    private const int MAX_PAYLOAD_BYTES = 32 * 1024;

    /// <summary>
    /// Stable error code returned when <see cref="MAX_PAYLOAD_BYTES"/> is
    /// exceeded. Callers (notably the daemon-facing event-record SignalR hub
    /// method) pattern-match on this so the error surfaces with a usable
    /// classification rather than a raw exception or a free-text string. The
    /// daemon logs and drops — there's nothing to retry against a fixed cap.
    /// </summary>
    public const string PayloadTooLargeErrorCode = "runtime.event.payload_too_large";

    private readonly ApplicationDbContext _db;
    private readonly IClock _clock;
    private readonly ILogger<RecordRuntimeEventCommandHandler> _logger;

    public RecordRuntimeEventCommandHandler(
        ApplicationDbContext db,
        IClock clock,
        ILogger<RecordRuntimeEventCommandHandler> logger)
    {
        _db = db;
        _clock = clock;
        _logger = logger;
    }

    public async Task<Result> Handle(
        RecordRuntimeEventCommand request,
        CancellationToken cancellationToken)
    {
        // Payload-size guard. We check BEFORE the try block so a too-large
        // payload returns the stable error code without going through the
        // exception path (which would also degrade to a generic message). The
        // byte count is over the UTF-8 encoding because that's the actual
        // serialised size on disk; counting chars would under-count multi-byte
        // sequences.
        var payloadBytes = string.IsNullOrEmpty(request.Payload)
            ? 0
            : System.Text.Encoding.UTF8.GetByteCount(request.Payload);
        if (payloadBytes > MAX_PAYLOAD_BYTES)
        {
            _logger.LogWarning(
                "RuntimeEvent payload over cap: runtime {RuntimeId}, type {Type}, bytes {Bytes} > {Max}. Event dropped.",
                request.RuntimeId,
                request.Type,
                payloadBytes,
                MAX_PAYLOAD_BYTES);
            return Result.Failure(PayloadTooLargeErrorCode);
        }

        try
        {
            var row = new RuntimeEvent
            {
                Id = Guid.NewGuid(),
                RuntimeId = request.RuntimeId,
                Type = request.Type,
                Severity = request.Severity,
                Timestamp = request.Timestamp,
                DurationMs = request.DurationMs,
                Payload = request.Payload,
                // CreatedAt / UpdatedAt are stamped by ApplicationDbContext.SaveChangesAsync
                // via the IAuditable contract; do not assign here.
            };

            _db.RuntimeEvents.Add(row);
            await _db.SaveChangesAsync(cancellationToken);

            await EnforceCapAsync(request.RuntimeId, cancellationToken);

            await BumpBootstrapActivityAsync(request.RuntimeId, cancellationToken);

            return Result.Success();
        }
        catch (Exception ex)
        {
            // Best-effort: persistence is observability, not load-bearing. Log
            // and return Failure so the caller can decide whether to surface
            // it — but we do NOT throw, because a daemon emitting a malformed
            // event must not crash the dispatcher pipeline.
            _logger.LogError(
                ex,
                "Failed to record RuntimeEvent: runtime {RuntimeId}, type {Type}",
                request.RuntimeId,
                request.Type);
            return Result.Failure($"Failed to record runtime event: {ex.Message}");
        }
    }

    /// <summary>
    /// Best-effort bootstrap-liveness signal for <c>HeartbeatWatcherJob</c>'s
    /// silence-based watchdog (self-healing-runtime-specs, card B4). When a
    /// runtime is mid-boot (<see cref="RuntimeState.Booting"/> /
    /// <see cref="RuntimeState.Bootstrapping"/> / <see cref="RuntimeState.Waking"/>),
    /// every recorded event is a proof-of-life that never otherwise reaches the
    /// <c>ProjectRuntime</c> row — bootstrap progress streams to RuntimeEvents
    /// only, leaving <c>UpdatedAt</c> frozen at the last state change. Without
    /// this bump a runtime busy-but-quiet on the row (a real .NET first boot —
    /// <c>dotnet restore</c> ~5 min + build) trips the bootstrap-timeout branch
    /// and respawn-loops forever. We stamp <c>LastBootstrapActivityAt = now</c>
    /// via a single server-side conditional <c>ExecuteUpdateAsync</c> — no entity
    /// load, and deliberately NOT touching <c>UpdatedAt</c> so the activity signal
    /// stays orthogonal to the audit timestamp.
    ///
    /// <para><b>Strictly best-effort.</b> Wrapped in try/catch and never throws:
    /// the same observability-not-load-bearing contract as event persistence
    /// itself. A failed bump must not fail the event record (the caller already
    /// got <see cref="Result.Success"/>), so we log and move on. The
    /// <c>State in (Booting, Bootstrapping, Waking)</c> predicate means a no-op
    /// (zero rows) for every Online / terminal runtime — the steady-state hot
    /// path costs one cheap conditional UPDATE that matches nothing.</para>
    /// </summary>
    private async Task BumpBootstrapActivityAsync(Guid runtimeId, CancellationToken ct)
    {
        try
        {
            var nowUtc = _clock.UtcNow;

            await _db.ProjectRuntimes
                .Where(r => r.Id == runtimeId
                         && (r.State == RuntimeState.Booting
                          || r.State == RuntimeState.Bootstrapping
                          || r.State == RuntimeState.Waking))
                .ExecuteUpdateAsync(
                    s => s.SetProperty(r => r.LastBootstrapActivityAt, nowUtc),
                    ct);
        }
        catch (Exception ex)
        {
            // Best-effort: the silence-watchdog signal is a liveness hint, not a
            // correctness requirement. The event already persisted; a failed bump
            // just means the watchdog falls back to UpdatedAt for this tick. Never
            // throw — the observability path must not break a working runtime.
            _logger.LogWarning(
                ex,
                "Failed to bump LastBootstrapActivityAt for mid-boot runtime {RuntimeId}; watchdog will fall back to UpdatedAt",
                runtimeId);
        }
    }

    /// <summary>
    /// Drop the oldest events past the rolling cap for this runtime. Done in
    /// one round-trip: a single <c>DELETE … WHERE Id IN (SELECT … OFFSET cap)</c>
    /// expressed in LINQ so EF emits the equivalent SQL on Postgres and the
    /// InMemory provider executes it via change-tracking. We tolerate up to a
    /// few rows of drift (a concurrent insert could land between the count
    /// check and the delete) — the cap is approximate-by-design and the next
    /// insert closes the gap.
    /// </summary>
    private async Task EnforceCapAsync(Guid runtimeId, CancellationToken ct)
    {
        var total = await _db.RuntimeEvents
            .Where(e => e.RuntimeId == runtimeId)
            .CountAsync(ct);

        if (total <= MAX_EVENTS_PER_RUNTIME)
        {
            return;
        }

        // The InMemory provider doesn't implement ExecuteDeleteAsync. Fall back
        // to the load-and-remove path there so unit tests can exercise the
        // full cap-enforcement contract. Production (Postgres) always takes
        // the bulk path.
        var excessCount = total - MAX_EVENTS_PER_RUNTIME;

        if (_db.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            var stale = await _db.RuntimeEvents
                .Where(e => e.RuntimeId == runtimeId)
                .OrderBy(e => e.Timestamp)
                .Take(excessCount)
                .ToListAsync(ct);
            _db.RuntimeEvents.RemoveRange(stale);
            await _db.SaveChangesAsync(ct);
            return;
        }

        // Bulk path. The subquery is needed because Postgres doesn't allow a
        // self-reference in a DELETE's USING clause that also has an ORDER BY
        // — pushing the ORDER BY / Skip into a subquery is the canonical idiom
        // and EF translates it cleanly.
        var idsToDelete = _db.RuntimeEvents
            .Where(e => e.RuntimeId == runtimeId)
            .OrderBy(e => e.Timestamp)
            .Take(excessCount)
            .Select(e => e.Id);

        await _db.RuntimeEvents
            .Where(e => idsToDelete.Contains(e.Id))
            .ExecuteDeleteAsync(ct);
    }
}

using System.Text.Json;
using Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Source.Features.RuntimeEvents.Commands;
using Source.Features.RuntimeEvents.Models;

// JsonDocument used only for read-side payload assertions.
#pragma warning disable IDE0005

namespace Api.Tests.Features.RuntimeEvents;

/// <summary>
/// Unit tests for <see cref="RecordRuntimeEventCommandHandler"/> — the
/// foundation handler for the V2 runtime event store. Exercises the four
/// invariants the persistence layer has to honour:
///
/// <list type="bullet">
///   <item>Single insert is retrievable by <c>RuntimeId</c> (round-trip works).</item>
///   <item><c>DurationMs</c> persists correctly when present and when null —
///         it's the top-level column that drives the "slowest" queries, so
///         we can't let it desync from the payload.</item>
///   <item><c>Severity</c> round-trips as a string via the
///         <c>HasConversion&lt;string&gt;()</c> mapping.</item>
///   <item>The rolling FIFO cap (5000) is enforced: a 5100-event insert leaves
///         exactly 5000 rows behind, and the *oldest* 100 are the ones evicted
///         (FIFO, not random).</item>
///   <item>Caps are per-runtime — events on runtime A never evict events on
///         runtime B.</item>
/// </list>
/// </summary>
public class RecordRuntimeEventCommandHandlerTests : HandlerTestBase
{
    private RecordRuntimeEventCommandHandler CreateHandler() => new(
        Context,
        new FakeClock(),
        NullLogger<RecordRuntimeEventCommandHandler>.Instance);

    /// <summary>
    /// Helper that returns the payload string verbatim. The handler takes a
    /// string (matching the codebase's jsonb convention) so this is a
    /// straight pass-through; the wrapper keeps the test call sites
    /// expressive ("Payload(...)") and gives us one place to swap shape later.
    /// </summary>
    private static string Payload(string json = "{}") => json;

    /// <summary>
    /// Parses a persisted payload string for round-trip assertions. The
    /// caller scopes the JsonDocument's lifetime — fine because the document
    /// is dropped at the end of each assertion expression.
    /// </summary>
    private static JsonDocument Parse(string json) => JsonDocument.Parse(json);

    // ----------------------------------------------------------------------
    // Single-event insert round-trip
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Inserts_SingleEvent_Retrievable_ByRuntimeId()
    {
        var runtimeId = Guid.NewGuid();
        var handler = CreateHandler();

        var result = await handler.Handle(
            new RecordRuntimeEventCommand(
                RuntimeId: runtimeId,
                Type: RuntimeEventTypes.InstallStarted,
                Severity: RuntimeEventSeverity.Info,
                Timestamp: new DateTime(2026, 5, 12, 9, 0, 0, DateTimeKind.Utc),
                DurationMs: null,
                Payload: Payload("""{"hash":"abc123"}""")),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        var rows = await Context.RuntimeEvents
            .Where(e => e.RuntimeId == runtimeId)
            .ToListAsync();

        rows.Should().HaveCount(1);
        rows[0].RuntimeId.Should().Be(runtimeId);
        rows[0].Type.Should().Be(RuntimeEventTypes.InstallStarted);
        rows[0].Timestamp.Should().Be(new DateTime(2026, 5, 12, 9, 0, 0, DateTimeKind.Utc));
        Parse(rows[0].Payload).RootElement.GetProperty("hash").GetString().Should().Be("abc123");
    }

    // ----------------------------------------------------------------------
    // DurationMs round-trips (present + null)
    // ----------------------------------------------------------------------

    [Fact]
    public async Task DurationMs_PersistedCorrectly_WhenPresent()
    {
        var runtimeId = Guid.NewGuid();
        var handler = CreateHandler();

        await handler.Handle(
            new RecordRuntimeEventCommand(
                RuntimeId: runtimeId,
                Type: RuntimeEventTypes.InstallCompleted,
                Severity: RuntimeEventSeverity.Info,
                Timestamp: DateTime.UtcNow,
                DurationMs: 42_000L,
                Payload: Payload()),
            CancellationToken.None);

        var row = await Context.RuntimeEvents.SingleAsync(e => e.RuntimeId == runtimeId);
        row.DurationMs.Should().Be(42_000L);
    }

    [Fact]
    public async Task DurationMs_PersistedCorrectly_WhenNull()
    {
        var runtimeId = Guid.NewGuid();
        var handler = CreateHandler();

        await handler.Handle(
            new RecordRuntimeEventCommand(
                RuntimeId: runtimeId,
                Type: RuntimeEventTypes.InstallStarted,
                Severity: RuntimeEventSeverity.Info,
                Timestamp: DateTime.UtcNow,
                DurationMs: null,
                Payload: Payload()),
            CancellationToken.None);

        var row = await Context.RuntimeEvents.SingleAsync(e => e.RuntimeId == runtimeId);
        row.DurationMs.Should().BeNull();
    }

    // ----------------------------------------------------------------------
    // Severity round-trip via string conversion
    // ----------------------------------------------------------------------

    [Theory]
    [InlineData(RuntimeEventSeverity.Info)]
    [InlineData(RuntimeEventSeverity.Warn)]
    [InlineData(RuntimeEventSeverity.Error)]
    public async Task Severity_RoundTrips(RuntimeEventSeverity severity)
    {
        var runtimeId = Guid.NewGuid();
        var handler = CreateHandler();

        await handler.Handle(
            new RecordRuntimeEventCommand(
                RuntimeId: runtimeId,
                Type: RuntimeEventTypes.ServiceCrashed,
                Severity: severity,
                Timestamp: DateTime.UtcNow,
                DurationMs: null,
                Payload: Payload()),
            CancellationToken.None);

        // Re-read through a fresh tracker so we test the read path, not the
        // entity that's still in the change tracker from the insert.
        Context.ChangeTracker.Clear();

        var row = await Context.RuntimeEvents.SingleAsync(e => e.RuntimeId == runtimeId);
        row.Severity.Should().Be(severity);
    }

    // ----------------------------------------------------------------------
    // Rolling-cap enforcement
    // ----------------------------------------------------------------------

    [Fact]
    public async Task RollingCap_EnforcedAt5000_OldestEventsEvicted()
    {
        var runtimeId = Guid.NewGuid();
        var handler = CreateHandler();
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // 5100 events. The first 100 should be evicted; events 101..5100 should remain.
        for (var i = 0; i < 5_100; i++)
        {
            await handler.Handle(
                new RecordRuntimeEventCommand(
                    RuntimeId: runtimeId,
                    Type: RuntimeEventTypes.InstallStarted,
                    Severity: RuntimeEventSeverity.Info,
                    // Tag each event with its ordinal as both timestamp offset
                    // and a payload field so we can verify FIFO ordering.
                    Timestamp: baseTime.AddSeconds(i),
                    DurationMs: i,
                    Payload: Payload($$"""{"ord":{{i}}}""")),
                CancellationToken.None);
        }

        var remaining = await Context.RuntimeEvents
            .Where(e => e.RuntimeId == runtimeId)
            .OrderBy(e => e.Timestamp)
            .ToListAsync();

        remaining.Should().HaveCount(5_000);

        // Oldest surviving event is ordinal 100 (the first 100 were evicted).
        remaining[0].DurationMs.Should().Be(100);
        Parse(remaining[0].Payload).RootElement.GetProperty("ord").GetInt32().Should().Be(100);

        // Newest surviving event is ordinal 5099.
        remaining[^1].DurationMs.Should().Be(5_099);
        Parse(remaining[^1].Payload).RootElement.GetProperty("ord").GetInt32().Should().Be(5_099);
    }

    // ----------------------------------------------------------------------
    // Cap is per-runtime (no cross-contamination)
    // ----------------------------------------------------------------------

    [Fact]
    public async Task RollingCap_PerRuntime_OtherRuntimesUnaffected()
    {
        var noisy = Guid.NewGuid();
        var quiet = Guid.NewGuid();
        var handler = CreateHandler();
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Quiet runtime — a handful of events that pre-date the noisy flood.
        for (var i = 0; i < 5; i++)
        {
            await handler.Handle(
                new RecordRuntimeEventCommand(
                    RuntimeId: quiet,
                    Type: RuntimeEventTypes.ServiceRunning,
                    Severity: RuntimeEventSeverity.Info,
                    Timestamp: baseTime.AddMinutes(i),
                    DurationMs: null,
                    Payload: Payload()),
                CancellationToken.None);
        }

        // Noisy runtime — past the cap.
        for (var i = 0; i < 5_050; i++)
        {
            await handler.Handle(
                new RecordRuntimeEventCommand(
                    RuntimeId: noisy,
                    Type: RuntimeEventTypes.InstallStarted,
                    Severity: RuntimeEventSeverity.Info,
                    // Noisy events come *after* the quiet runtime's so a buggy
                    // global cap would evict the quiet rows first — letting
                    // the test catch the mistake.
                    Timestamp: baseTime.AddHours(1).AddSeconds(i),
                    DurationMs: null,
                    Payload: Payload()),
                CancellationToken.None);
        }

        var noisyCount = await Context.RuntimeEvents
            .Where(e => e.RuntimeId == noisy)
            .CountAsync();
        var quietCount = await Context.RuntimeEvents
            .Where(e => e.RuntimeId == quiet)
            .CountAsync();

        noisyCount.Should().Be(5_000);
        quietCount.Should().Be(5);
    }
}

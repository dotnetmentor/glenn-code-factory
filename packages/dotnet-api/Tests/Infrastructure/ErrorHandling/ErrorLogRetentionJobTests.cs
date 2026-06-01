using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Source.Features.ErrorLog.Models;
using Source.Infrastructure.ErrorHandling;

namespace Api.Tests.Infrastructure.ErrorHandling;

/// <summary>
/// Tests for <see cref="ErrorLogRetentionJob"/> — the daily Hangfire job that deletes
/// old <see cref="ErrorLog"/> rows.
///
/// Invariants under test:
/// - Strict "<" boundary (row at exactly cutoff is kept).
/// - <see cref="ErrorSignature"/> rows are NEVER touched, regardless of age.
/// - Retention window is driven by <see cref="ErrorCaptureOptions.RetentionDays"/> and <see cref="Source.Shared.IClock"/>.
/// - Deleted-count is logged at Information level.
/// - Exceptions from the database bubble up so Hangfire can retry.
/// </summary>
public class ErrorLogRetentionJobTests : HandlerTestBase
{
    private static readonly DateTime FixedNow = new(2026, 4, 16, 12, 0, 0, DateTimeKind.Utc);

    private ErrorLogRetentionJob CreateJob(int retentionDays, out CapturingLogger<ErrorLogRetentionJob> logger)
    {
        logger = new CapturingLogger<ErrorLogRetentionJob>();
        var options = Options.Create(new ErrorCaptureOptions { RetentionDays = retentionDays });
        var clock = new FakeClock(FixedNow);
        return new ErrorLogRetentionJob(Context, options, clock, logger);
    }

    private async Task SeedErrorLogAsync(DateTime createdAt, string message = "log")
    {
        Context.ErrorLogs.Add(new ErrorLog
        {
            Id = Guid.NewGuid(),
            Message = message,
            Source = "Test",
            Severity = "Error",
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        });
        await Context.SaveChangesAsync();
    }

    private async Task SeedSignatureAsync(DateTime createdAt, string hash)
    {
        Context.ErrorSignatures.Add(new ErrorSignature
        {
            Id = Guid.NewGuid(),
            Hash = hash,
            Source = "Test",
            Severity = "Error",
            FirstSeenAt = createdAt,
            LastSeenAt = createdAt,
            Count = 1,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        });
        await Context.SaveChangesAsync();
    }

    [Fact]
    public async Task DeletesRowsOlderThanRetentionDays()
    {
        // Seed: one row older than retention (91d), two within retention (89d, 1d).
        await SeedErrorLogAsync(FixedNow.AddDays(-91), "old");
        await SeedErrorLogAsync(FixedNow.AddDays(-89), "recent-1");
        await SeedErrorLogAsync(FixedNow.AddDays(-1), "recent-2");

        var job = CreateJob(retentionDays: 90, out _);

        await job.ExecuteAsync(CancellationToken.None);

        var remaining = await Context.ErrorLogs.AsNoTracking().ToListAsync();
        remaining.Should().HaveCount(2);
        remaining.Select(e => e.Message).Should().BeEquivalentTo(new[] { "recent-1", "recent-2" });
    }

    [Fact]
    public async Task KeepsRowsAtExactBoundary()
    {
        // Row at exactly NOW - 90d should be kept (strict "<" comparison, not "<=").
        await SeedErrorLogAsync(FixedNow.AddDays(-90), "boundary");

        var job = CreateJob(retentionDays: 90, out _);

        await job.ExecuteAsync(CancellationToken.None);

        var remaining = await Context.ErrorLogs.AsNoTracking().ToListAsync();
        remaining.Should().HaveCount(1, "the boundary row is NOT older than cutoff (strict <)");
        remaining[0].Message.Should().Be("boundary");
    }

    [Fact]
    public async Task DoesNotDeleteErrorSignatures()
    {
        // Ancient signatures must survive.
        await SeedSignatureAsync(FixedNow.AddDays(-365), new string('a', 64));
        await SeedSignatureAsync(FixedNow.AddDays(-365), new string('b', 64));

        // Plus old ErrorLogs that WILL be deleted.
        await SeedErrorLogAsync(FixedNow.AddDays(-365), "very-old-log");
        await SeedErrorLogAsync(FixedNow.AddDays(-200), "old-log");

        var job = CreateJob(retentionDays: 90, out _);

        await job.ExecuteAsync(CancellationToken.None);

        var remainingSignatures = await Context.ErrorSignatures.AsNoTracking().ToListAsync();
        remainingSignatures.Should().HaveCount(2, "ErrorSignatures are never touched regardless of age");

        var remainingLogs = await Context.ErrorLogs.AsNoTracking().ToListAsync();
        remainingLogs.Should().BeEmpty("old ErrorLog rows should have been deleted");
    }

    [Fact]
    public async Task RespectsCustomRetentionDays()
    {
        // With RetentionDays=30, anything older than 30d should be deleted; 29d old should be kept.
        await SeedErrorLogAsync(FixedNow.AddDays(-31), "over-30");
        await SeedErrorLogAsync(FixedNow.AddDays(-29), "under-30");

        var job = CreateJob(retentionDays: 30, out _);

        await job.ExecuteAsync(CancellationToken.None);

        var remaining = await Context.ErrorLogs.AsNoTracking().ToListAsync();
        remaining.Should().HaveCount(1);
        remaining[0].Message.Should().Be("under-30");
    }

    [Fact]
    public async Task LogsDeletedCount()
    {
        // Seed 5 rows that will all be deleted.
        for (var i = 0; i < 5; i++)
        {
            await SeedErrorLogAsync(FixedNow.AddDays(-100 - i), $"old-{i}");
        }
        // Also seed signatures to prove invariant alongside logging.
        await SeedSignatureAsync(FixedNow.AddDays(-500), new string('c', 64));

        var job = CreateJob(retentionDays: 90, out var logger);

        await job.ExecuteAsync(CancellationToken.None);

        // ErrorSignatures untouched:
        (await Context.ErrorSignatures.AsNoTracking().CountAsync()).Should().Be(1);

        var infoLog = logger.Entries
            .FirstOrDefault(e => e.Level == LogLevel.Information && e.Message.Contains("5"));
        infoLog.Should().NotBeNull("the job should log the deleted count at Information level");
        infoLog!.Message.Should().Contain("ErrorLogRetentionJob");
    }

    [Fact]
    public async Task EmptyTable_ReturnsZeroAndSucceeds()
    {
        // No rows — job should complete cleanly and log zero.
        var job = CreateJob(retentionDays: 90, out var logger);

        var act = async () => await job.ExecuteAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();

        var infoLog = logger.Entries
            .FirstOrDefault(e => e.Level == LogLevel.Information && e.Message.Contains("0"));
        infoLog.Should().NotBeNull("zero-deletes must still be logged at Information");
    }

    [Fact]
    public async Task DatabaseException_BubblesUp()
    {
        // Dispose the context so any DB operation throws ObjectDisposedException.
        // This simulates a DB failure — Hangfire relies on exceptions propagating
        // to trigger its retry mechanism.
        Context.Dispose();

        var logger = new CapturingLogger<ErrorLogRetentionJob>();
        var options = Options.Create(new ErrorCaptureOptions { RetentionDays = 90 });
        var clock = new FakeClock(FixedNow);
        var job = new ErrorLogRetentionJob(Context, options, clock, logger);

        var act = async () => await job.ExecuteAsync(CancellationToken.None);

        await act.Should().ThrowAsync<Exception>(
            "DB exceptions must propagate so Hangfire can retry");

        logger.Entries.Should().Contain(e => e.Level == LogLevel.Error,
            "the failure should be logged before re-throw");
    }

    [Fact]
    public async Task DoesNotDeleteSignatures_EvenWhenAllLogsDeleted()
    {
        // Explicit double-down on the signature invariant: even when the job
        // deletes every single ErrorLog, signatures of any age remain untouched.
        await SeedSignatureAsync(FixedNow.AddDays(-1000), new string('d', 64));
        await SeedErrorLogAsync(FixedNow.AddDays(-100), "will-be-deleted");

        var job = CreateJob(retentionDays: 90, out _);

        await job.ExecuteAsync(CancellationToken.None);

        (await Context.ErrorLogs.AsNoTracking().CountAsync()).Should().Be(0);
        (await Context.ErrorSignatures.AsNoTracking().CountAsync()).Should().Be(1,
            "ErrorSignatures must survive a job run that deletes every ErrorLog");
    }

    // ---- Minimal ILogger test double ----

    /// <summary>
    /// Captures log entries so tests can assert on level and formatted message.
    /// </summary>
    private class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        IDisposable ILogger.BeginScope<TState>(TState state) => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}

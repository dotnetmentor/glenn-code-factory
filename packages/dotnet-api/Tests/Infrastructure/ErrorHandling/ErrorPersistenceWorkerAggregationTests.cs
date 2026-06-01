using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Source.Features.ErrorLog.Models;
using Source.Infrastructure;
using Source.Infrastructure.ErrorHandling;
using Source.Shared;

namespace Api.Tests.Infrastructure.ErrorHandling;

/// <summary>
/// Tests for per-batch signature aggregation inside <see cref="ErrorPersistenceWorker.FlushAsync"/>.
///
/// These tests drive the worker's flush path DIRECTLY — rather than round-tripping through
/// the queue + background loop — because signature aggregation is a deterministic
/// in-process transform: "given this batch, produce these DB effects". Running it via the
/// full worker would add the non-determinism of batch-collection timing for no gain.
/// <see cref="ErrorPersistenceWorker.FlushAsync"/> is <c>internal</c> for exactly this reason;
/// <c>InternalsVisibleTo Api.Tests</c> is already set on the production assembly.
///
/// <para><b>Time control:</b> every assertion about <c>FirstSeenAt</c>, <c>LastSeenAt</c>,
/// <c>CreatedAt</c>, <c>UpdatedAt</c> reads through the injected <see cref="FakeClock"/>.
/// No wall-clock anywhere in this file.</para>
///
/// <para><b>Counter isolation:</b> <see cref="ErrorPipelineMetrics"/> is a static process-
/// wide counter. We always capture the value BEFORE the arrange step and assert the DELTA,
/// never the absolute — so these tests remain green regardless of ordering.</para>
/// </summary>
public class ErrorPersistenceWorkerAggregationTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly string _dbName;

    public ErrorPersistenceWorkerAggregationTests()
    {
        _dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseInMemoryDatabase(_dbName));
        _provider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _provider.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------

    private ApplicationDbContext NewReadContext()
        => _provider.CreateScope().ServiceProvider.GetRequiredService<ApplicationDbContext>();

    private ErrorPersistenceWorker BuildWorker(FakeClock clock)
    {
        var queue = new ErrorQueue(new PiiRedactor());
        var scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();
        return new ErrorPersistenceWorker(
            queue,
            scopeFactory,
            NullLogger<ErrorPersistenceWorker>.Instance,
            clock,
            new ErrorSignatureHasher(),
            new ErrorPersistenceWorkerOptions
            {
                MaxBatchSize = 100,
                BatchTimeout = TimeSpan.FromMilliseconds(25),
            });
    }

    /// <summary>
    /// Build an entry whose stack trace produces a STABLE hash across calls. The hasher
    /// uses Source + ExceptionType + top-3 frames, so as long as those are identical, the
    /// hash is identical. Different <paramref name="typeName"/> values produce different hashes.
    /// </summary>
    private static ErrorEntry StableEntry(
        string typeName = "System.InvalidOperationException",
        string message = "boom",
        DateTime? occurredAt = null)
    {
        var stack = $"{typeName}: {message}\n"
                  + "   at Foo.Bar.Baz.DoWork() in Foo/Bar/Baz.cs:line 10\n"
                  + "   at Foo.Bar.Baz.CallerOne() in Foo/Bar/Baz.cs:line 20\n"
                  + "   at Foo.Bar.Baz.CallerTwo() in Foo/Bar/Baz.cs:line 30";
        return new ErrorEntry(
            Message: message,
            StackTrace: stack,
            Source: "HTTP",
            Severity: "Error",
            CorrelationId: null,
            RequestPath: null,
            RequestMethod: null,
            ContextData: null,
            OccurredAt: occurredAt ?? new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc));
    }

    // ---------------------------------------------------------------------------------
    // Tests
    // ---------------------------------------------------------------------------------

    [Fact]
    public async Task NewSignature_CreatesSignatureAndErrorLog_WithLinkedFk()
    {
        // Arrange
        var clock = new FakeClock(new DateTime(2030, 5, 1, 10, 0, 0, DateTimeKind.Utc));
        var worker = BuildWorker(clock);
        var entry = StableEntry();

        // Act
        await worker.FlushAsync(new List<ErrorEntry> { entry }, CancellationToken.None);

        // Assert
        using var db = NewReadContext();
        var sig = await db.ErrorSignatures.SingleAsync();
        sig.Count.Should().Be(1);
        sig.FirstSeenAt.Should().Be(clock.UtcNow);
        sig.LastSeenAt.Should().Be(clock.UtcNow);
        sig.Source.Should().Be("HTTP");
        sig.Severity.Should().Be("Error");
        sig.IsResolved.Should().BeFalse();
        sig.Hash.Length.Should().Be(64);

        var log = await db.ErrorLogs.SingleAsync();
        log.SignatureId.Should().Be(sig.Id,
            "every persisted detail row must be linked to the signature row for this batch");
    }

    [Fact]
    public async Task SecondOccurrence_UpdatesSignatureCount_AdvancesLastSeenAt()
    {
        // Arrange: first batch lands at t=0, creates the signature.
        var clock = new FakeClock(new DateTime(2030, 5, 1, 10, 0, 0, DateTimeKind.Utc));
        var firstSeen = clock.UtcNow;
        var worker = BuildWorker(clock);
        await worker.FlushAsync(new List<ErrorEntry> { StableEntry() }, CancellationToken.None);

        // Act: advance the clock 10s, flush another occurrence of the same hash.
        clock.Advance(TimeSpan.FromSeconds(10));
        var secondSeen = clock.UtcNow;
        await worker.FlushAsync(new List<ErrorEntry> { StableEntry() }, CancellationToken.None);

        // Assert
        using var db = NewReadContext();
        var sig = await db.ErrorSignatures.SingleAsync();
        sig.Count.Should().Be(2);
        sig.FirstSeenAt.Should().Be(firstSeen, "first-seen is write-once");
        sig.LastSeenAt.Should().Be(secondSeen, "last-seen advances on every occurrence");

        (await db.ErrorLogs.CountAsync()).Should().Be(2);
        (await db.ErrorLogs.CountAsync(e => e.SignatureId == sig.Id)).Should().Be(2);
    }

    [Fact]
    public async Task ElevenOccurrences_Across_ElevenBatches_CapsAtTenSamples()
    {
        // Arrange
        var clock = new FakeClock();
        var worker = BuildWorker(clock);
        var suppressedBefore = ErrorPipelineMetrics.Suppressed;

        // Act: 11 single-entry batches, all the same hash. First 10 land as ErrorLog rows;
        // the 11th must bump the signature count but be suppressed as a detail row.
        for (var i = 0; i < 11; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            await worker.FlushAsync(new List<ErrorEntry> { StableEntry() }, CancellationToken.None);
        }

        // Assert
        using var db = NewReadContext();
        var sig = await db.ErrorSignatures.SingleAsync();
        sig.Count.Should().Be(11, "every occurrence increments the signature count");

        (await db.ErrorLogs.CountAsync()).Should().Be(10,
            "the rolling-sample cap is 10 ErrorLog rows per signature");

        (ErrorPipelineMetrics.Suppressed - suppressedBefore).Should().BeGreaterThanOrEqualTo(1,
            "at least one detail row was suppressed by the cap");
    }

    [Fact]
    public async Task DifferentHashes_InSameBatch_CreateTwoSignatures()
    {
        // Arrange
        var clock = new FakeClock();
        var worker = BuildWorker(clock);

        var a = StableEntry(typeName: "System.InvalidOperationException");
        var b = StableEntry(typeName: "System.ArgumentNullException");

        // Act
        await worker.FlushAsync(new List<ErrorEntry> { a, b }, CancellationToken.None);

        // Assert
        using var db = NewReadContext();
        (await db.ErrorSignatures.CountAsync()).Should().Be(2);
        (await db.ErrorLogs.CountAsync()).Should().Be(2);

        var hashes = await db.ErrorSignatures.Select(s => s.Hash).ToListAsync();
        hashes.Distinct().Count().Should().Be(2, "two different exception types must produce two hashes");

        // Every ErrorLog must be linked to SOME signature, and the two signatures must be
        // the two that exist in the DB (belt-and-suspenders against accidental null FK).
        var logSignatureIds = await db.ErrorLogs.Select(e => e.SignatureId).ToListAsync();
        logSignatureIds.Should().AllSatisfy(id => id.Should().NotBeNull());
    }

    [Fact]
    public async Task FiveSameHashEntries_InOneBatch_OneSignatureCount5_FiveErrorLogs()
    {
        // Arrange
        var clock = new FakeClock();
        var worker = BuildWorker(clock);

        var batch = Enumerable.Range(0, 5).Select(_ => StableEntry()).ToList();

        // Act
        await worker.FlushAsync(batch, CancellationToken.None);

        // Assert
        using var db = NewReadContext();
        var sig = await db.ErrorSignatures.SingleAsync();
        sig.Count.Should().Be(5);
        (await db.ErrorLogs.CountAsync()).Should().Be(5);
        (await db.ErrorLogs.CountAsync(e => e.SignatureId == sig.Id)).Should().Be(5);
    }

    [Fact]
    public async Task FifteenSameHashEntries_InOneBatch_NewSignature_TenErrorLogs()
    {
        // Arrange
        var clock = new FakeClock();
        var worker = BuildWorker(clock);
        var suppressedBefore = ErrorPipelineMetrics.Suppressed;

        var batch = Enumerable.Range(0, 15).Select(_ => StableEntry()).ToList();

        // Act
        await worker.FlushAsync(batch, CancellationToken.None);

        // Assert: one signature with Count=15, but only 10 detail rows persisted.
        using var db = NewReadContext();
        var sig = await db.ErrorSignatures.SingleAsync();
        sig.Count.Should().Be(15);
        (await db.ErrorLogs.CountAsync()).Should().Be(10);

        (ErrorPipelineMetrics.Suppressed - suppressedBefore).Should().Be(5,
            "15 entries − 10 persisted = 5 suppressed");
    }

    [Fact]
    public async Task LastSeenAt_UsesInjectedClock()
    {
        // Arrange: fix the fake clock at a very specific non-today instant so drift from
        // DateTime.UtcNow would be instantly visible in the assertion.
        var fixedInstant = new DateTime(2099, 7, 14, 3, 14, 15, DateTimeKind.Utc);
        var clock = new FakeClock(fixedInstant);
        var worker = BuildWorker(clock);

        // Act
        await worker.FlushAsync(new List<ErrorEntry> { StableEntry() }, CancellationToken.None);

        // Assert
        using var db = NewReadContext();
        var sig = await db.ErrorSignatures.SingleAsync();
        sig.FirstSeenAt.Should().Be(fixedInstant);
        sig.LastSeenAt.Should().Be(fixedInstant);
        sig.CreatedAt.Should().Be(fixedInstant);
        sig.UpdatedAt.Should().Be(fixedInstant);
    }

    [Fact]
    public async Task SignatureUpsert_AfterExistingTenSamples_AddsNoNewErrorLogs()
    {
        // Arrange: pre-seed a signature and 10 ErrorLog rows already linked to it, then
        // flush 3 more occurrences of the same hash. Expect: count moves +3, suppressed
        // counter moves +3, no new detail rows.
        var clock = new FakeClock();
        var worker = BuildWorker(clock);
        var seedEntry = StableEntry();
        var hasher = new ErrorSignatureHasher();
        var hash = hasher.Hash(seedEntry);

        var signatureId = Guid.NewGuid();
        using (var seedDb = NewReadContext())
        {
            seedDb.ErrorSignatures.Add(new ErrorSignature
            {
                Id = signatureId,
                Hash = hash,
                Source = "HTTP",
                Severity = "Error",
                FirstSeenAt = clock.UtcNow,
                LastSeenAt = clock.UtcNow,
                Count = 10,
                CreatedAt = clock.UtcNow,
                UpdatedAt = clock.UtcNow,
            });
            for (var i = 0; i < 10; i++)
            {
                seedDb.ErrorLogs.Add(new ErrorLog
                {
                    Id = Guid.NewGuid(),
                    Message = "seed",
                    Source = "HTTP",
                    Severity = "Error",
                    SignatureId = signatureId,
                    CreatedAt = clock.UtcNow,
                    UpdatedAt = clock.UtcNow,
                });
            }
            await seedDb.SaveChangesAsync();
        }

        var suppressedBefore = ErrorPipelineMetrics.Suppressed;

        // Act
        clock.Advance(TimeSpan.FromMinutes(1));
        var batch = Enumerable.Range(0, 3).Select(_ => StableEntry()).ToList();
        await worker.FlushAsync(batch, CancellationToken.None);

        // Assert
        using var db = NewReadContext();
        (await db.ErrorLogs.CountAsync()).Should().Be(10,
            "the sample cap is already reached — no additional detail rows should be added");

        var sig = await db.ErrorSignatures.SingleAsync(s => s.Hash == hash);
        sig.Count.Should().Be(13, "count must keep climbing even when detail rows are suppressed");
        sig.LastSeenAt.Should().Be(clock.UtcNow, "LastSeenAt advances on every flush, regardless of cap");

        (ErrorPipelineMetrics.Suppressed - suppressedBefore).Should().Be(3);
    }
}

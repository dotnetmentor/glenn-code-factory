using Source.Infrastructure.ErrorHandling;

namespace Api.Tests.Infrastructure.ErrorHandling;

/// <summary>
/// Tests for <see cref="ErrorQueue"/>'s bounded-capacity, drop-oldest behaviour, drop
/// accounting, and its hard never-throw contract on <see cref="ErrorQueue.EnqueueAsync"/>.
///
/// The guiding principle: an error logger that can fail its host is worse than no error
/// logger at all. EnqueueAsync must swallow every exception.
/// </summary>
public class ErrorQueueTests
{
    private static ErrorEntry NewEntry(string message) => new(
        Message: message,
        StackTrace: null,
        Source: "Test",
        Severity: "Error",
        CorrelationId: null,
        RequestPath: null,
        RequestMethod: null,
        ContextData: null,
        OccurredAt: DateTime.UtcNow);

    private static ErrorQueue NewQueue() => new(new PiiRedactor());

    [Fact]
    public async Task EnqueueUnderCapacity_AllReadableInFifoOrder()
    {
        var queue = NewQueue();
        const int count = 5000;

        for (var i = 0; i < count; i++)
        {
            await queue.EnqueueAsync(NewEntry($"msg-{i}"));
        }
        queue.CompleteWriting();

        var read = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var entry in queue.ReadAllAsync(cts.Token))
        {
            read.Add(entry.Message);
        }

        read.Should().HaveCount(count);
        read[0].Should().Be("msg-0");
        read[^1].Should().Be($"msg-{count - 1}");
    }

    [Fact]
    public async Task EnqueueOverCapacity_MaintainsCapacityAndIncrementsDroppedCount()
    {
        var queue = NewQueue();
        const int overflow = 15_000;
        const int capacity = 10_000;

        for (var i = 0; i < overflow; i++)
        {
            await queue.EnqueueAsync(NewEntry($"msg-{i}"));
        }
        queue.CompleteWriting();

        var read = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var entry in queue.ReadAllAsync(cts.Token))
        {
            read.Add(entry.Message);
        }

        // Bounded at capacity
        read.Should().HaveCount(capacity);

        // DropOldest semantics: the newer entries survive. msg-14999 must be present;
        // msg-0 must have been dropped.
        read.Should().Contain($"msg-{overflow - 1}");
        read.Should().NotContain("msg-0");

        // Drop accounting — allow some slack as count may be best-effort.
        queue.DroppedCount.Should().BeGreaterThanOrEqualTo(overflow - capacity - 100);
    }

    [Fact]
    public async Task EnqueueAsync_DoesNotThrow_AfterWriterCompleted()
    {
        var queue = NewQueue();
        queue.CompleteWriting();

        var threw = false;
        try
        {
            await queue.EnqueueAsync(NewEntry("post-complete"));
        }
        catch
        {
            threw = true;
        }

        threw.Should().BeFalse("EnqueueAsync must never throw — that's the hard contract");
    }

    [Fact]
    public void DroppedCount_StartsAtZero()
    {
        var queue = NewQueue();

        queue.DroppedCount.Should().Be(0);
    }

    [Fact]
    public async Task ConcurrentProducers_NoExceptions_TotalAccountedFor()
    {
        var queue = NewQueue();
        const int producers = 10;
        const int perProducer = 1000;
        const int total = producers * perProducer;

        var producerTasks = Enumerable.Range(0, producers).Select(p => Task.Run(async () =>
        {
            for (var i = 0; i < perProducer; i++)
            {
                await queue.EnqueueAsync(NewEntry($"p{p}-i{i}"));
            }
        })).ToArray();

        await Task.WhenAll(producerTasks);
        queue.CompleteWriting();

        var read = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await foreach (var _ in queue.ReadAllAsync(cts.Token))
        {
            read++;
        }

        // Within slack: read + dropped should equal total enqueue attempts.
        var accounted = read + queue.DroppedCount;
        accounted.Should().BeInRange(total - 200, total + 200);
    }

    [Fact]
    public async Task Enqueue_WithEmailInMessage_PersistsRedactedText()
    {
        var queue = NewQueue();

        await queue.EnqueueAsync(NewEntry("user alice@example.com hit 500"));
        queue.CompleteWriting();

        ErrorEntry? read = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var entry in queue.ReadAllAsync(cts.Token))
        {
            read = entry;
            break;
        }

        read.Should().NotBeNull();
        read!.Message.Should().Contain("<email>");
        read.Message.Should().NotContain("alice@");
    }
}

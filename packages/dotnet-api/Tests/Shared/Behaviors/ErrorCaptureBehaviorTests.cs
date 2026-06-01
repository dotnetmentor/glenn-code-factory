using System.Diagnostics;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Source.Infrastructure.ErrorHandling;
using Source.Shared.Behaviors;

namespace Api.Tests.Shared.Behaviors;

/// <summary>
/// Tests for <see cref="ErrorCaptureBehavior{TRequest,TResponse}"/>.
///
/// The behavior's single responsibility is: when a downstream handler throws, enqueue an
/// <see cref="ErrorEntry"/> describing the failure and then rethrow the ORIGINAL exception.
/// It must never mask a handler exception with its own pipeline failures.
/// </summary>
public class ErrorCaptureBehaviorTests
{
    // --- Test helpers -------------------------------------------------------

    public record FakeCommand(int X) : IRequest<string>;

    private static ErrorQueue NewQueue() => new(new PiiRedactor());

    private static ErrorCaptureBehavior<FakeCommand, string> NewBehavior(ErrorQueue queue) =>
        new(queue, NullLogger<ErrorCaptureBehavior<FakeCommand, string>>.Instance);

    private static async Task<List<ErrorEntry>> DrainAsync(ErrorQueue queue)
    {
        queue.CompleteWriting();
        var collected = new List<ErrorEntry>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var e in queue.ReadAllAsync(cts.Token))
        {
            collected.Add(e);
        }
        return collected;
    }

    // --- 1. Handler throws → entry enqueued with Source="Handler" ----------

    [Fact]
    public async Task Handler_Throws_EnqueuesErrorWithSourceHandler()
    {
        var queue = NewQueue();
        var behavior = NewBehavior(queue);
        var boom = new InvalidOperationException("boom");

        var act = async () => await behavior.Handle(
            new FakeCommand(1),
            ct => throw boom,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();

        var entries = await DrainAsync(queue);
        entries.Should().HaveCount(1);
        entries[0].Source.Should().Be("Handler");
        entries[0].Severity.Should().Be("Error");
        entries[0].Message.Should().Be("boom");
    }

    // --- 2. ContextData == typeof(TRequest).Name ---------------------------

    [Fact]
    public async Task ContextData_EqualsRequestTypeName()
    {
        var queue = NewQueue();
        var behavior = NewBehavior(queue);

        var act = async () => await behavior.Handle(
            new FakeCommand(1),
            ct => throw new InvalidOperationException("x"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        var entries = await DrainAsync(queue);
        entries[0].ContextData.Should().Be(nameof(FakeCommand));
    }

    // --- 3. Original exception is rethrown (same instance, preserved stack) -

    [Fact]
    public async Task Original_Exception_Rethrown_SameInstance()
    {
        var queue = NewQueue();
        var behavior = NewBehavior(queue);
        var boom = new InvalidOperationException("original");

        Exception? caught = null;
        try
        {
            await behavior.Handle(
                new FakeCommand(1),
                ct => throw boom,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        caught.Should().NotBeNull();
        ReferenceEquals(boom, caught).Should().BeTrue(
            "the behavior must rethrow the exact same exception instance, not a copy or wrapper");
        caught!.StackTrace.Should().NotBeNullOrEmpty("preserved via 'throw;' not 'throw ex;'");
    }

    // --- 4. Happy path: no enqueue ----------------------------------------

    [Fact]
    public async Task SuccessfulHandler_NoEnqueue()
    {
        var queue = NewQueue();
        var behavior = NewBehavior(queue);

        var result = await behavior.Handle(
            new FakeCommand(1),
            ct => Task.FromResult("ok"),
            CancellationToken.None);

        result.Should().Be("ok");
        var entries = await DrainAsync(queue);
        entries.Should().BeEmpty();
    }

    // --- 5. Activity.Current → CorrelationId ------------------------------

    [Fact]
    public async Task CorrelationId_FromActivity()
    {
        // Enable an ActivitySource and a listener so StartActivity actually returns a live
        // Activity (without a registered listener it short-circuits to null).
        using var source = new ActivitySource("ErrorCaptureBehaviorTests");
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = source.StartActivity("op");
        activity.Should().NotBeNull("listener guarantees the activity is live");

        var expectedTraceId = activity!.TraceId.ToString();

        var queue = NewQueue();
        var behavior = NewBehavior(queue);

        var act = async () => await behavior.Handle(
            new FakeCommand(1),
            ct => throw new InvalidOperationException("boom"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        var entries = await DrainAsync(queue);
        entries[0].CorrelationId.Should().Be(expectedTraceId);
    }

    // --- 6. Defense in depth: enqueue throw must NOT mask handler exception -

    [Fact]
    public async Task EnqueueAsyncThrows_OriginalExceptionStillRethrown()
    {
        var queue = new BrokenQueue();
        var behavior = new ErrorCaptureBehavior<FakeCommand, string>(
            queue,
            NullLogger<ErrorCaptureBehavior<FakeCommand, string>>.Instance);

        var original = new InvalidOperationException("original");

        Exception? caught = null;
        try
        {
            await behavior.Handle(
                new FakeCommand(1),
                ct => throw original,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        caught.Should().BeSameAs(original,
            "a broken enqueue path must NEVER mask the handler's exception");
    }

    /// <summary>
    /// Subclass that forces the behavior's inner catch to fire by overriding
    /// <see cref="ErrorQueue.EnqueueAsync"/> to throw. This is the only way to exercise
    /// the behavior's defense-in-depth inner try/catch, since the production
    /// <see cref="ErrorQueue"/> honors a never-throw contract.
    /// </summary>
    private sealed class BrokenQueue : ErrorQueue
    {
        public BrokenQueue() : base(new PiiRedactor()) { }

        public override ValueTask EnqueueAsync(ErrorEntry error)
        {
            throw new Exception("queue broken");
        }
    }

    // --- 7. No Activity → CorrelationId null ------------------------------

    [Fact]
    public async Task NoActivity_CorrelationIdIsNull()
    {
        // Defensive: detach any ambient Activity the test harness might have set.
        var prior = Activity.Current;
        Activity.Current = null;
        try
        {
            var queue = NewQueue();
            var behavior = NewBehavior(queue);

            var act = async () => await behavior.Handle(
                new FakeCommand(1),
                ct => throw new InvalidOperationException("boom"),
                CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>();
            var entries = await DrainAsync(queue);
            entries[0].CorrelationId.Should().BeNull();
        }
        finally
        {
            Activity.Current = prior;
        }
    }
}

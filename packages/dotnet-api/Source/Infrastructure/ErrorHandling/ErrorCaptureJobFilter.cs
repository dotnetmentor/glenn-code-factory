using System.Text.Json;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.States;
using Hangfire.Storage;

namespace Source.Infrastructure.ErrorHandling;

/// <summary>
/// Hangfire filter that captures <b>terminal</b> job failures into the <see cref="ErrorQueue"/>.
///
/// <para>Why both <see cref="IServerFilter"/> and <see cref="IApplyStateFilter"/>?</para>
/// <list type="bullet">
///   <item>
///     <see cref="IServerFilter"/> is what the spec card asks for — it's the canonical
///     per-execution hook (<c>OnPerforming</c> / <c>OnPerformed</c>) and gives us
///     <see cref="PerformedContext.Exception"/> on the failing attempt.
///   </item>
///   <item>
///     <see cref="IApplyStateFilter"/> is what lets us gate capture on <i>terminal</i>
///     failure only. <see cref="IServerFilter.OnPerformed"/> fires on every attempt that
///     threw, including transient attempts that Hangfire's <c>AutomaticRetryAttribute</c>
///     will re-schedule. By reacting to <c>OnStateApplied</c> with
///     <c>NewState is FailedState</c>, we fire exactly once per job — when retries are
///     exhausted and the job actually lands in <see cref="FailedState"/>.
///   </item>
/// </list>
///
/// <para>The filter <b>must never throw</b>. Letting an exception escape into Hangfire's
/// state machine can corrupt job state or take down the server. All logic is wrapped in a
/// catch-all and failures are swallowed by design — an error logger that breaks its host
/// is worse than no error logger at all.</para>
/// </summary>
public sealed class ErrorCaptureJobFilter : IServerFilter, IApplyStateFilter
{
    private readonly ErrorQueue _queue;

    public ErrorCaptureJobFilter(ErrorQueue queue)
    {
        _queue = queue;
    }

    // IServerFilter is part of the Hangfire execution pipeline. We don't need to do anything
    // around the actual job execution — the terminal-failure capture happens in OnStateApplied
    // below — but keeping IServerFilter on the class satisfies the spec's wiring shape and
    // ensures the filter shows up in per-execution filter lookups.

    public void OnPerforming(PerformingContext filterContext)
    {
        // no-op
    }

    public void OnPerformed(PerformedContext filterContext)
    {
        // no-op — see class docstring. Capture happens in OnStateApplied so that
        // transient-retry attempts don't double-count.
    }

    /// <summary>
    /// Called once per state transition. We care only about the transition <i>into</i>
    /// <see cref="FailedState"/> — that means all retries are exhausted and the failure
    /// is terminal. Hangfire's <c>AutomaticRetryAttribute</c> implements
    /// <see cref="IElectStateFilter"/> and rewrites the candidate state from
    /// <see cref="FailedState"/> to <see cref="ScheduledState"/> when retries remain,
    /// so by the time <c>OnStateApplied</c> sees <see cref="FailedState"/> it is final.
    /// </summary>
    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        try
        {
            if (context.NewState is not FailedState failedState)
            {
                return;
            }

            var jobName = SafeJobName(context);
            var queueName = SafeQueueName(context);
            var retryCount = SafeRetryCount(context);
            var arguments = SafeArguments(context);

            var contextData = SerializeContext(jobName, queueName, retryCount, arguments);

            var entry = new ErrorEntry(
                Message: failedState.Exception?.Message ?? "(no message)",
                StackTrace: failedState.Exception?.StackTrace,
                Source: "Hangfire",
                Severity: "Error",
                CorrelationId: context.BackgroundJob?.Id,
                RequestPath: null,
                RequestMethod: null,
                ContextData: contextData,
                OccurredAt: DateTime.UtcNow);

            // EnqueueAsync has a hard never-throw contract, so GetAwaiter().GetResult()
            // is safe here even though we're on a sync Hangfire callback thread.
            _queue.EnqueueAsync(entry).GetAwaiter().GetResult();
        }
        catch
        {
            // Defence in depth: ErrorQueue.EnqueueAsync already swallows its own failures,
            // but a bug in property access on ApplyStateContext shouldn't be able to corrupt
            // Hangfire's state machine. Swallow silently — the pipeline's own health counters
            // are the feedback loop if this ever matters.
        }
    }

    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        // no-op — we don't track un-applications.
    }

    // ---- Safe metadata accessors ----

    private static string SafeJobName(ApplyStateContext context)
    {
        try
        {
            var job = context.BackgroundJob?.Job;
            if (job?.Type is null || job.Method is null)
            {
                return "(unknown)";
            }
            return $"{job.Type.Name}.{job.Method.Name}";
        }
        catch
        {
            return "(unknown)";
        }
    }

    private static string SafeQueueName(ApplyStateContext context)
    {
        try
        {
            // Jobs carry their queue in a job parameter (set by EnqueuedState at creation).
            var q = context.Connection?.GetJobParameter(context.BackgroundJob!.Id, "Queue");
            if (!string.IsNullOrWhiteSpace(q))
            {
                // Hangfire serialises parameters as JSON, so the stored value is a quoted string.
                return q.Trim('"');
            }
        }
        catch
        {
            // fall through
        }
        return "default";
    }

    private static int SafeRetryCount(ApplyStateContext context)
    {
        try
        {
            var raw = context.Connection?.GetJobParameter(context.BackgroundJob!.Id, "RetryCount");
            if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw.Trim('"'), out var n))
            {
                return n;
            }
        }
        catch
        {
            // fall through
        }
        return 0;
    }

    private static string[] SafeArguments(ApplyStateContext context)
    {
        try
        {
            var args = context.BackgroundJob?.Job?.Args;
            if (args is null)
            {
                return Array.Empty<string>();
            }
            return args.Select(a => a?.ToString() ?? "null").ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string SerializeContext(string jobName, string queue, int retryCount, string[] arguments)
    {
        try
        {
            return JsonSerializer.Serialize(new
            {
                JobName = jobName,
                Queue = queue,
                RetryCount = retryCount,
                Arguments = arguments,
            });
        }
        catch
        {
            // If serialization somehow throws, fall back to a minimal payload so we still
            // capture the job identity. Never throw out of the filter.
            return $"{{\"JobName\":\"{jobName}\",\"Queue\":\"{queue}\",\"RetryCount\":{retryCount}}}";
        }
    }
}

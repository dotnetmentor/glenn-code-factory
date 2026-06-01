using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Source.Infrastructure.ErrorHandling;

/// <summary>
/// EF Core <see cref="SaveChangesInterceptor"/> that captures failed <c>SaveChanges</c> calls
/// and enqueues them onto the <see cref="ErrorQueue"/> for persistence by the background worker.
///
/// <para><b>Scope.</b> Only failures are captured — successful saves pass straight through. The
/// interceptor overrides <see cref="SaveChangesInterceptor.SaveChangesFailed"/> and
/// <see cref="SaveChangesInterceptor.SaveChangesFailedAsync"/>, calls <c>base</c> after its work
/// so the ORIGINAL exception continues to propagate to the caller unchanged.</para>
///
/// <para><b>PII contract (hard rule).</b> <see cref="ErrorEntry.ContextData"/> is a JSON array of
/// the DISTINCT <see cref="Type.Name"/> values of the entities affected by the failed save — and
/// nothing else. We never read property values, key values, foreign-key values, or any other
/// field off the tracked entity. That keeps us mechanically safe from leaking emails,
/// password hashes, API keys, or any other column contents into the error log, even if the
/// schema changes. This is the mitigation called out in the resilient-error-capture-pipeline
/// spec: "the interceptor must not be a stealth data exfiltrator."</para>
///
/// <para><b>Defense in depth.</b> The entire capture path is wrapped in a try/catch that logs
/// and swallows — an interceptor MUST NOT throw a secondary exception that masks the original
/// <see cref="DbUpdateException"/>. If enqueue ever fails, the user still gets the real error;
/// we just lose the observability entry.</para>
///
/// <para><b>Registered as a singleton</b> — depends only on <see cref="ErrorQueue"/> (singleton)
/// and <see cref="ILogger{TCategoryName}"/> (singleton-safe).</para>
/// </summary>
public sealed class ErrorCaptureSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly ErrorQueue _queue;
    private readonly ILogger<ErrorCaptureSaveChangesInterceptor> _logger;

    public ErrorCaptureSaveChangesInterceptor(
        ErrorQueue queue,
        ILogger<ErrorCaptureSaveChangesInterceptor> logger)
    {
        _queue = queue;
        _logger = logger;
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        Capture(eventData);
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Capture(eventData);
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    /// <summary>
    /// Builds the <see cref="ErrorEntry"/> describing the failed save and hands it to the queue.
    /// Internal for test access — see <c>ErrorCaptureSaveChangesInterceptorTests</c>.
    ///
    /// <para>Guarantees:</para>
    /// <list type="bullet">
    ///   <item>Never throws — any exception is logged and swallowed.</item>
    ///   <item>Never reads entity property values — only <c>GetType().Name</c>.</item>
    ///   <item><see cref="ErrorEntry.ContextData"/> is a JSON array of distinct, sorted type names.</item>
    /// </list>
    /// </summary>
    internal void Capture(DbContextErrorEventData eventData)
    {
        try
        {
            var affectedTypes = eventData.Context?.ChangeTracker
                .Entries()
                .Where(e => e.State != EntityState.Unchanged && e.State != EntityState.Detached)
                .Select(e => e.Entity.GetType().Name)
                .Distinct()
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray() ?? Array.Empty<string>();

            var entry = new ErrorEntry(
                Message: eventData.Exception.Message,
                StackTrace: eventData.Exception.StackTrace,
                Source: "Database",
                Severity: "Error",
                CorrelationId: Activity.Current?.TraceId.ToString(),
                RequestPath: null,
                RequestMethod: null,
                ContextData: JsonSerializer.Serialize(affectedTypes),
                OccurredAt: DateTime.UtcNow
            );

            // EnqueueAsync has a never-throw guarantee (see ErrorQueue); the work inside is a
            // synchronous Channel.TryWrite so GetAwaiter().GetResult() is safe and does not
            // block on real async I/O. We use it here so that a non-async interceptor override
            // (SaveChangesFailed) can share the same capture path as the async one.
            _queue.EnqueueAsync(entry).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // Defense in depth — never let the interceptor's own failure mask the real
            // DbUpdateException that EF is about to rethrow to the caller.
            _logger.LogError(
                ex,
                "ErrorCaptureSaveChangesInterceptor failed to enqueue — propagating original exception unchanged");
        }
    }
}

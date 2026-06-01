using MediatR;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.SignalR.Contracts;
using Source.Infrastructure;
using Source.Shared;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimeLifecycle.Commands;

/// <summary>
/// Persists a single daemon-supplied error report as a <see cref="RuntimeErrorReport"/>
/// row. Routed via <see cref="SignalR.Hubs.RuntimeHub.ReportError"/> after
/// the hub method has projected the connection-level <c>rt_runtime</c> claim
/// into <see cref="RuntimeId"/> and pulled the typed payload off the wire.
///
/// <list type="bullet">
///   <item>Append-only — there is no FK to <c>ProjectRuntime</c> and no
///         soft-delete. Mirrors <c>BootstrapRun</c> / <c>RuntimeStateEvent</c>:
///         the audit trail must outlive the runtime row, and hiding diagnostic
///         rows defeats the whole point of having an error feed.</item>
///   <item>The hub validates the claim; the command does <b>not</b> re-check
///         that <see cref="RuntimeId"/> still names a live runtime — a daemon
///         that races a janitor hard-delete should still get its last error
///         persisted (operators want the row, not a 404 swallowed silently).</item>
///   <item>Server-side caps: <c>Category</c> 64 chars, <c>Message</c> 4000
///         chars, <c>StackTrace</c> + <c>Context</c> 16000 chars each. The
///         daemon enforces the same limits in the
///         <see cref="ErrorReportPayload"/> shape; we truncate defensively
///         here in case a malformed daemon ships past them.</item>
///   <item><see cref="RuntimeErrorReport.ReportedAt"/> stamps the server's
///         UTC at receive — the source of truth for "when did this arrive?"
///         A daemon with a skewed clock can't shuffle the timeline. Same
///         rationale as <c>ProjectRuntime.LastHeartbeatAt</c>.</item>
/// </list>
///
/// <para>No domain event raised — there is nothing for other slices to react
/// to. If a future card adds a "Notify operators on hot error categories"
/// flow, this is the right place to raise it.</para>
/// </summary>
public record ReportRuntimeErrorCommand(
    Guid RuntimeId,
    ErrorReportPayload Payload) : ICommand<Result<Unit>>;

public class ReportRuntimeErrorCommandHandler
    : ICommandHandler<ReportRuntimeErrorCommand, Result<Unit>>
{
    private readonly ApplicationDbContext _db;
    private readonly IClock _clock;
    private readonly ILogger<ReportRuntimeErrorCommandHandler> _logger;

    public ReportRuntimeErrorCommandHandler(
        ApplicationDbContext db,
        IClock clock,
        ILogger<ReportRuntimeErrorCommandHandler> logger)
    {
        _db = db;
        _clock = clock;
        _logger = logger;
    }

    public async Task<Result<Unit>> Handle(
        ReportRuntimeErrorCommand request,
        CancellationToken cancellationToken)
    {
        var payload = request.Payload;

        // Defensive truncation — the wire validators on ErrorReportPayload
        // already cap these at the daemon side, but a malformed / older
        // daemon shouldn't be able to OOM the persistence path.
        var category = Truncate(payload.Category ?? string.Empty, 64);
        var message = Truncate(payload.Message ?? string.Empty, 4000);
        var stackTrace = payload.StackTrace is null ? null : Truncate(payload.StackTrace, 16000);
        var context = payload.Context is null ? null : Truncate(payload.Context, 16000);

        var row = new RuntimeErrorReport
        {
            Id = Guid.NewGuid(),
            RuntimeId = request.RuntimeId,
            Category = category,
            Message = message,
            StackTrace = stackTrace,
            Context = context,
            ReportedAt = _clock.UtcNow,
            // CreatedAt / UpdatedAt are set by the AuditableEntityInterceptor;
            // do not assign here.
        };

        _db.RuntimeErrorReports.Add(row);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "RuntimeErrorReport persisted: runtime {RuntimeId}, category {Category}, id {ErrorId}",
            request.RuntimeId, category, row.Id);

        return Result.Success(Unit.Value);
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max);
}

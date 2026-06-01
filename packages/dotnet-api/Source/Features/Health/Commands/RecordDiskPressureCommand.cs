using MediatR;
using Microsoft.EntityFrameworkCore;
using Source.Features.Health.Models;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;
using Source.Infrastructure;
using Source.Shared;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Health.Commands;

/// <summary>
/// Persists a single daemon-reported disk-pressure transition as a
/// <see cref="RuntimeDiskPressureEvent"/> row, then fans the event out to the
/// <c>project-{ProjectId}</c> SignalR group via
/// <see cref="IAgentClient.RuntimeDiskPressure"/> so the project dashboard's
/// banner refreshes without polling.
///
/// <para>Routed via <c>RuntimeHub.ReportDiskPressure</c> after the hub
/// projects the connection-level <c>rt_runtime</c> claim into
/// <see cref="RuntimeId"/> and pulls the typed payload off the wire.</para>
///
/// <list type="bullet">
///   <item>Append-only — there is no FK to <c>ProjectRuntime</c> and no
///         soft-delete. Mirrors <see cref="Source.Features.RuntimeLifecycle.Commands.ReportRuntimeErrorCommand"/>
///         and <c>BootstrapRun</c>: the audit trail must outlive the runtime
///         row, and hiding diagnostic rows defeats the whole point of having
///         a pressure timeline.</item>
///   <item>The hub validates the runtime claim; the command does <b>not</b>
///         re-check that <see cref="RuntimeId"/> still names a live runtime —
///         the row should land regardless. The fan-out path needs a
///         <c>ProjectId</c> for group routing, however, so we look up the
///         (possibly soft-deleted) runtime once. If the runtime row has been
///         hard-deleted in the racy interval between the daemon emit and the
///         server persist, we still record the audit row but skip the
///         broadcast — there is no project group left to push to.</item>
///   <item>Server-side cap: <see cref="DiskPressurePayload.Level"/> 16 chars.
///         The daemon only emits <c>"ok" | "warn" | "critical"</c> today; the
///         cap is a defensive guard against a malformed daemon shipping a
///         long string. Bytes / pct fields are numeric and don't need
///         truncation.</item>
///   <item><see cref="RuntimeDiskPressureEvent.ReportedAt"/> stamps the
///         server's UTC at receive — the source of truth for ordering. The
///         daemon's <see cref="DiskPressurePayload.SampledAt"/> is preserved
///         on the row for clock-skew telemetry but never used for ordering,
///         same rationale as <c>ProjectRuntime.LastHeartbeatAt</c>.</item>
/// </list>
///
/// <para>No domain event raised — the only consumer of this signal today is
/// the broadcast handler, which we invoke directly from the command since the
/// fan-out is a hub push (not persisted state). If a future card adds a
/// "trigger janitor on critical pressure" flow, this is the right place to
/// raise it.</para>
/// </summary>
public record RecordDiskPressureCommand(
    Guid RuntimeId,
    DiskPressurePayload Payload) : ICommand<Result<Unit>>;

public class RecordDiskPressureCommandHandler
    : ICommandHandler<RecordDiskPressureCommand, Result<Unit>>
{
    private readonly ApplicationDbContext _db;
    private readonly Microsoft.AspNetCore.SignalR.IHubContext<AgentHub, IAgentClient> _agentHub;
    private readonly IClock _clock;
    private readonly ILogger<RecordDiskPressureCommandHandler> _logger;

    public RecordDiskPressureCommandHandler(
        ApplicationDbContext db,
        Microsoft.AspNetCore.SignalR.IHubContext<AgentHub, IAgentClient> agentHub,
        IClock clock,
        ILogger<RecordDiskPressureCommandHandler> logger)
    {
        _db = db;
        _agentHub = agentHub;
        _clock = clock;
        _logger = logger;
    }

    public async Task<Result<Unit>> Handle(
        RecordDiskPressureCommand request,
        CancellationToken cancellationToken)
    {
        var payload = request.Payload;
        var level = Truncate(payload.Level ?? string.Empty, 16);

        var now = _clock.UtcNow;

        var row = new RuntimeDiskPressureEvent
        {
            Id = Guid.NewGuid(),
            RuntimeId = request.RuntimeId,
            Level = level,
            UsedBytes = payload.UsedBytes,
            TotalBytes = payload.TotalBytes,
            UsedPct = payload.UsedPct,
            SampledAt = payload.SampledAt,
            ReportedAt = now,
            // CreatedAt / UpdatedAt are set by the AuditableEntityInterceptor;
            // do not assign here.
        };

        _db.RuntimeDiskPressureEvents.Add(row);
        await _db.SaveChangesAsync(cancellationToken);

        // Look up the project so we can fan out to the project group. We use
        // IgnoreQueryFilters so a soft-deleted runtime still returns its
        // ProjectId — the broadcast is operationally useful even if the
        // runtime row has been retired (the project page may still be open).
        // A hard-deleted runtime — which the audit trail explicitly outlives
        // — has no ProjectId to broadcast against; skip in that case.
        var projectId = await _db.ProjectRuntimes
            .IgnoreQueryFilters()
            .Where(r => r.Id == request.RuntimeId)
            .Select(r => (Guid?)r.ProjectId)
            .FirstOrDefaultAsync(cancellationToken);

        if (projectId is null)
        {
            _logger.LogInformation(
                "RuntimeDiskPressureEvent persisted for runtime {RuntimeId} (level={Level}) but the runtime row has been hard-deleted; skipping fan-out.",
                request.RuntimeId, level);
            return Result.Success(Unit.Value);
        }

        var notification = new RuntimeDiskPressureNotification(
            RuntimeId: request.RuntimeId,
            ProjectId: projectId.Value,
            Level: level,
            UsedBytes: row.UsedBytes,
            TotalBytes: row.TotalBytes,
            UsedPct: row.UsedPct,
            SampledAt: row.SampledAt,
            ReportedAt: row.ReportedAt);

        try
        {
            await _agentHub.Clients
                .Group($"project-{projectId.Value}")
                .RuntimeDiskPressure(notification);
        }
        catch (Exception ex)
        {
            // Same swallow-and-warn pattern as the other broadcast handlers.
            // The audit row already landed; a missed fan-out is a transient UX
            // gap (the dashboard page resync will catch up on next reload),
            // not data loss.
            _logger.LogWarning(ex,
                "Failed to broadcast RuntimeDiskPressure for runtime {RuntimeId} to project {ProjectId}.",
                request.RuntimeId, projectId.Value);
        }

        _logger.LogInformation(
            "RuntimeDiskPressureEvent persisted: runtime {RuntimeId}, level {Level}, usedPct {UsedPct:F2}, id {EventId}",
            request.RuntimeId, level, row.UsedPct, row.Id);

        return Result.Success(Unit.Value);
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max);
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Source.Features.Health.Models;
using Source.Infrastructure;

namespace Source.Features.Health.Controllers;

/// <summary>
/// User-facing read endpoint for the in-memory daemon-health rolling buffer
/// (Phase D Card 1). Returns up to <see cref="HealthSnapshotBuffer.Capacity"/>
/// snapshots for one runtime, optionally filtered to those received after
/// the supplied <c>since</c> timestamp so the project page can long-poll a
/// trailing tail without re-fetching the whole window.
///
/// <para><b>Why no MediatR.</b> Same pragmatic reasoning as the rest of the
/// runtime controllers (status, admin): one read against an in-memory
/// singleton + one DB existence check. Wrapping in a query handler adds
/// indirection without benefit.</para>
///
/// <para><b>Authorisation.</b> <see cref="AuthorizeAttribute"/> only — same
/// as <see cref="RuntimeLifecycle.Controllers.RuntimeStatusController"/>.
/// Project-ownership gating ships when the Project entity does. The 404 vs
/// 200-empty distinction is intentional: a 404 means "no such runtime" so
/// the UI can show "no runtime" affordance, while 200 with an empty list
/// means "runtime exists but no telemetry yet" (typical for a freshly
/// connected daemon — first heartbeat hasn't landed yet).</para>
/// </summary>
[ApiController]
[Route("api/runtimes/{runtimeId:guid}/health-snapshots")]
[Authorize]
[Tags("RuntimeHealth")]
public class RuntimeHealthController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly HealthSnapshotBuffer _buffer;

    public RuntimeHealthController(ApplicationDbContext db, HealthSnapshotBuffer buffer)
    {
        _db = db;
        _buffer = buffer;
    }

    /// <summary>
    /// Return the in-memory health snapshots collected for the runtime,
    /// oldest-first. <paramref name="since"/> is optional: when supplied,
    /// only snapshots with <c>ReceivedAt &gt; since</c> are returned.
    ///
    /// <para>404 if the runtime row doesn't exist (or is soft-deleted —
    /// the global query filter on <see cref="ProjectRuntime"/> handles
    /// that). 200 with an empty list if the runtime exists but the buffer
    /// is empty (daemon hasn't connected / first heartbeat hasn't landed
    /// yet).</para>
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<HealthSnapshotDto>), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<List<HealthSnapshotDto>>> GetSnapshots(
        Guid runtimeId,
        [FromQuery] DateTime? since,
        CancellationToken ct)
    {
        var runtimeExists = await _db.ProjectRuntimes
            .AnyAsync(r => r.Id == runtimeId, ct);
        if (!runtimeExists)
        {
            return NotFound();
        }

        var snapshots = _buffer.ReadSince(runtimeId, since);

        var dtos = new List<HealthSnapshotDto>(snapshots.Count);
        foreach (var s in snapshots)
        {
            dtos.Add(new HealthSnapshotDto(
                ReceivedAt: s.ReceivedAt,
                CpuPct: s.CpuPct,
                MemUsedMb: s.MemUsedMb,
                DiskUsedPct: s.DiskUsedPct,
                SupervisedServicesUp: s.SupervisedServicesUp,
                ActiveSessionId: s.ActiveSessionId));
        }

        return Ok(dtos);
    }
}

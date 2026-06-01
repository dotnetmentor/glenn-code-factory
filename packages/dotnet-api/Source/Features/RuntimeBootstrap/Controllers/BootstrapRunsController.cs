using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeBootstrap.Models;
using Source.Infrastructure;
using Source.Infrastructure.AuthorizationModels;

namespace Source.Features.RuntimeBootstrap.Controllers;

/// <summary>
/// Operator-only HTTP surface over the <see cref="BootstrapRun"/> audit table. Backs
/// the admin UI / CLI tooling for diagnosing failed or stuck runtime boots — the
/// daemon writes one row per attempt (deferred until the daemon-architecture spec
/// lands) and operators read those rows here.
///
/// <para><b>Why no MediatR.</b> Same pragmatic reasoning as
/// <see cref="Source.Features.FlyManagement.Controllers.FlyAdminController"/>: a thin
/// passthrough over an audit table is not a business feature. Wrapping the two
/// queries in commands/handlers would add four files without changing the behaviour.
/// The slice stays thin and the controller talks straight to the DbContext.</para>
///
/// <para>Authorisation: <see cref="RoleConstants.SuperAdmin"/>, matching every other
/// admin surface (FlyAdmin, RuntimeImages, SystemSettings). TenantAdmin would be too
/// broad — these rows can leak per-runtime details across tenants.</para>
/// </summary>
[ApiController]
[Route("api/admin/bootstrap-runs")]
[Authorize(Roles = RoleConstants.SuperAdmin)]
[Tags("BootstrapRuns")]
public class BootstrapRunsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<BootstrapRunsController> _logger;

    public BootstrapRunsController(
        ApplicationDbContext db,
        ILogger<BootstrapRunsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Page through the <see cref="BootstrapRun"/> audit log. Filters compose with AND.
    /// <paramref name="stage"/> matches the <see cref="BootstrapStage"/> enum
    /// case-insensitively; unknown values are silently ignored so a typo doesn't 400
    /// the operator. <paramref name="pageSize"/> is hard-capped at 200 — bigger pages
    /// just waste memory and risk timing out the request.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(BootstrapRunsResponse), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<BootstrapRunsResponse>> List(
        [FromQuery] Guid? runtimeId,
        [FromQuery] bool? success,
        [FromQuery] string? stage,
        [FromQuery] DateTime? since,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        // Defensive defaults — negative page numbers and zero/negative page sizes are
        // pure user error; coerce rather than 400. 200 is the cap to keep payloads
        // under control even when ErrorReason carries a 4000-char stack trace.
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 50;
        pageSize = Math.Min(pageSize, 200);

        var query = _db.BootstrapRuns.AsQueryable();

        if (runtimeId.HasValue)
        {
            query = query.Where(r => r.RuntimeId == runtimeId.Value);
        }

        if (success.HasValue)
        {
            query = query.Where(r => r.Success == success.Value);
        }

        if (!string.IsNullOrEmpty(stage)
            && Enum.TryParse<BootstrapStage>(stage, ignoreCase: true, out var parsed))
        {
            query = query.Where(r => r.FinalStage == parsed);
        }

        if (since.HasValue)
        {
            query = query.Where(r => r.StartedAt >= since.Value);
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(r => r.StartedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        _logger.LogInformation(
            "Admin listed BootstrapRuns (page={Page}, pageSize={PageSize}, count={Count}, total={Total})",
            page, pageSize, items.Count, total);

        return Ok(new BootstrapRunsResponse(items, total, page, pageSize));
    }

    /// <summary>
    /// Fetch a single <see cref="BootstrapRun"/> by its primary key. Returns 404 when
    /// the id doesn't match an existing row — operators occasionally paste stale
    /// guids out of dashboards and a 404 is the clearest signal.
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(BootstrapRun), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<BootstrapRun>> GetById(Guid id, CancellationToken ct)
    {
        var run = await _db.BootstrapRuns.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run is null)
        {
            return NotFound();
        }
        return Ok(run);
    }
}

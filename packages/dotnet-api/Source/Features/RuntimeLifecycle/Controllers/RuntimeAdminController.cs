using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Source.Features.FlyManagement;
using Source.Features.RuntimeLifecycle.Drift;
using Source.Features.RuntimeLifecycle.FlySnapshot;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;
using Source.Infrastructure.AuthorizationModels;

namespace Source.Features.RuntimeLifecycle.Controllers;

/// <summary>
/// Operator-only HTTP surface for the runtime lifecycle: list / inspect runtimes and
/// punch them through state transitions when a runtime gets stuck or needs surgery
/// (reset a Failed runtime, force-suspend a runaway, force-delete from any state).
///
/// <para><b>Why no MediatR.</b> Same pragmatic reasoning as
/// <see cref="Source.Features.FlyManagement.Controllers.FlyAdminController"/> and
/// <see cref="Source.Features.RuntimeBootstrap.Controllers.BootstrapRunsController"/>:
/// every endpoint is a thin passthrough — two reads for the queries, one
/// <see cref="ProjectRuntime.TransitionTo"/> call + SaveChanges for the operator
/// actions. The state graph and audit-write live on the entity / event handler already,
/// so wrapping the controller in commands would add four files per endpoint without
/// changing behaviour.</para>
///
/// <para><b>Authorisation.</b> <see cref="RoleConstants.SuperAdmin"/>, matching every
/// other admin surface (FlyAdmin, BootstrapRuns, RuntimeImages, SystemSettings). These
/// endpoints can destroy paid infrastructure — TenantAdmin would be too broad.</para>
/// </summary>
[ApiController]
[Route("api/admin/runtimes")]
[Authorize(Roles = RoleConstants.SuperAdmin)]
[Tags("RuntimeAdmin")]
public class RuntimeAdminController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly FlyClient _fly;
    private readonly ILogger<RuntimeAdminController> _logger;

    public RuntimeAdminController(
        ApplicationDbContext db,
        FlyClient fly,
        ILogger<RuntimeAdminController> logger)
    {
        _db = db;
        _fly = fly;
        _logger = logger;
    }

    // ----------------------------------------------------------------------
    // List + detail
    // ----------------------------------------------------------------------

    /// <summary>
    /// Page through <see cref="ProjectRuntime"/> rows. Filters compose with AND.
    /// <paramref name="state"/> matches the <see cref="RuntimeState"/> enum case-
    /// insensitively; unknown values are silently ignored so a typo doesn't 400 the
    /// operator. <paramref name="pageSize"/> is hard-capped at 200 — bigger pages just
    /// waste memory and risk timing out the request.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(RuntimesListResponse), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<RuntimesListResponse>> List(
        [FromQuery] string? state,
        [FromQuery] Guid? projectId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        // Defensive defaults — negative page numbers and zero/negative page sizes are
        // pure user error; coerce rather than 400. 200 is the cap to keep payloads
        // bounded.
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 50;
        pageSize = Math.Min(pageSize, 200);

        var query = _db.ProjectRuntimes.AsQueryable();

        if (!string.IsNullOrEmpty(state)
            && Enum.TryParse<RuntimeState>(state, ignoreCase: true, out var parsed))
        {
            query = query.Where(r => r.State == parsed);
        }

        if (projectId.HasValue)
        {
            query = query.Where(r => r.ProjectId == projectId.Value);
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(r => r.UpdatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        _logger.LogInformation(
            "Admin listed ProjectRuntimes (page={Page}, pageSize={PageSize}, count={Count}, total={Total})",
            page, pageSize, items.Count, total);

        return Ok(new RuntimesListResponse(items, total, page, pageSize));
    }

    /// <summary>
    /// Fetch a single <see cref="ProjectRuntime"/> by id, plus the most recent 50
    /// lifecycle transitions for it. 404 when the runtime is missing or soft-deleted —
    /// the global query filter hides deleted rows, which matches the operator UX
    /// (deleted runtimes belong in the audit trail, not the active runtimes view).
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(RuntimeDetailResponse), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<RuntimeDetailResponse>> GetById(Guid id, CancellationToken ct)
    {
        var runtime = await _db.ProjectRuntimes.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (runtime is null)
        {
            return NotFound();
        }

        // 50 rows is enough to see a crash-loop or a suspend/wake oscillation while
        // keeping the JSON payload bounded even with 4 KB metadata blobs inlined.
        var recent = await _db.RuntimeStateEvents
            .Where(e => e.RuntimeId == runtime.Id)
            .OrderByDescending(e => e.CreatedAt)
            .Take(50)
            .Select(e => new RuntimeTransitionDto(
                e.FromState,
                e.ToState,
                e.Reason,
                e.TriggeredBy,
                e.CreatedAt))
            .ToListAsync(ct);

        return Ok(new RuntimeDetailResponse(runtime, recent));
    }

    // ----------------------------------------------------------------------
    // Drift snapshot
    // ----------------------------------------------------------------------

    /// <summary>
    /// Tier-1 read-only operator monitoring: side-by-side snapshot of every
    /// <see cref="ProjectRuntime"/>'s DB state vs Fly's live machine state, plus
    /// any unaccounted-for Fly machines surfaced as orphans. The
    /// <c>RuntimeReconcilerJob</c> already nudges fixable drift back into line
    /// every 60 s; this endpoint shows the operator the same diff in real time,
    /// including the cases the reconciler can't auto-fix (illegal transitions,
    /// orphan Fly machines, stuck transitions).
    ///
    /// <para><b>Cost.</b> One DB query and one Fly <c>ListMachines</c> call per
    /// hit. No pagination yet — at our current scale the runtime row count is
    /// in the dozens; once it grows we'll add filters server-side rather than
    /// paginating, since the operator UX wants the full overview in one view.</para>
    ///
    /// <para><b>Fly outage handling.</b> A <see cref="FlyApiException"/> from
    /// <c>ListMachines</c> means our upstream view is unavailable — we return
    /// 502 Bad Gateway rather than 500, since the API itself is healthy and the
    /// operator needs the distinction to triage. Transport-level exceptions
    /// (DNS / timeout) get the same treatment.</para>
    /// </summary>
    [HttpGet("drift")]
    [ProducesResponseType(typeof(RuntimeDriftListResponse), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(502)]
    public async Task<ActionResult<RuntimeDriftListResponse>> Drift(
        [FromServices] IRuntimeDriftQueryService driftService,
        CancellationToken ct)
    {
        try
        {
            var snapshot = await driftService.BuildSnapshotAsync(ct);

            _logger.LogInformation(
                "Admin fetched runtime drift snapshot: total={Total} drift={Drift}",
                snapshot.TotalCount, snapshot.DriftCount);

            return Ok(snapshot);
        }
        catch (FlyApiException ex)
        {
            // Fly itself rejected the list call. Surface as 502 so the operator
            // UI can show "upstream unavailable" without it looking like our own
            // service crashed — 500 would imply the latter.
            _logger.LogWarning(
                ex,
                "Runtime drift snapshot failed: Fly ListMachines returned {StatusCode} {ErrorCode}",
                ex.StatusCode, ex.ErrorCode);
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                error = "Upstream Fly API unavailable; drift snapshot could not be generated.",
                upstreamStatusCode = ex.StatusCode,
            });
        }
        catch (HttpRequestException ex)
        {
            // Transport-level failure (DNS, timeout, connection reset).
            // Same intent as the FlyApiException branch — distinguish upstream
            // problems from real 500s.
            _logger.LogWarning(ex, "Runtime drift snapshot failed: transport error reaching Fly API");
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                error = "Upstream Fly API unreachable; drift snapshot could not be generated.",
            });
        }
    }

    // ----------------------------------------------------------------------
    // Fly snapshot (single runtime "reality check")
    // ----------------------------------------------------------------------

    /// <summary>
    /// Operator "reality check" for a single runtime: side-by-side dump of what our DB
    /// thinks the runtime looks like, what Fly's machines API reports, and the last 20
    /// <see cref="Source.Features.FlyManagement.Models.FlyOperation"/> rows targeting
    /// the runtime. Powers the Fly tab in the project-workspace debug panel.
    ///
    /// <para><b>Why this isn't just <see cref="Drift"/> filtered to one row.</b> Drift
    /// shows the cluster-wide view with rule evaluation. This endpoint goes the other
    /// direction — drills into one runtime with the raw payloads (request/response JSON,
    /// machine instance id, private IP) the operator needs once they've picked the row
    /// they're triaging. Different shape, different audience.</para>
    ///
    /// <para><b>Fly outage handling.</b> Unlike <see cref="Drift"/>, a Fly failure here
    /// does NOT bubble to a 502 — the service catches and nulls
    /// <see cref="FlySnapshotResponse.FlyView"/>. The DB half + ops timeline is exactly
    /// what the operator needs to triage an upstream incident; refusing the whole
    /// response when Fly is the broken thing would defeat the panel's purpose.</para>
    /// </summary>
    [HttpGet("{runtimeId:guid}/fly-snapshot")]
    [ProducesResponseType(typeof(FlySnapshotResponse), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<FlySnapshotResponse>> FlySnapshot(
        Guid runtimeId,
        [FromServices] IRuntimeFlySnapshotService flySnapshotService,
        CancellationToken ct)
    {
        var snapshot = await flySnapshotService.GetAsync(runtimeId, ct);
        if (snapshot is null)
        {
            return NotFound();
        }

        _logger.LogInformation(
            "Admin fetched Fly snapshot for runtime {RuntimeId}",
            runtimeId);

        return Ok(snapshot);
    }

    // ----------------------------------------------------------------------
    // Operator transitions — Reset / ForceSuspend / ForceDelete
    // ----------------------------------------------------------------------

    /// <summary>
    /// Move a <see cref="RuntimeState.Failed"/> runtime back to
    /// <see cref="RuntimeState.Pending"/> so it can be re-provisioned. Returns 409 when
    /// the runtime is in any other state — the state graph (see
    /// <see cref="RuntimeStateMachine"/>) only allows Failed → Pending; calling reset on
    /// a healthy runtime is almost always an operator mistake and a hard error is the
    /// clearest signal.
    /// </summary>
    [HttpPost("{id:guid}/reset")]
    [ProducesResponseType(typeof(ProjectRuntime), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    [ProducesResponseType(409)]
    public Task<ActionResult<ProjectRuntime>> Reset(Guid id, CancellationToken ct)
        => TransitionAsync(id, RuntimeState.Pending, "operator:reset", ct);

    /// <summary>
    /// Move an <see cref="RuntimeState.Online"/> (or other suspending-eligible) runtime
    /// to <see cref="RuntimeState.Suspending"/>. Used when a runaway or misbehaving
    /// runtime needs to be parked without going through the idler. Returns 409 when the
    /// state graph forbids the transition (e.g. already Suspended, or still Pending).
    /// </summary>
    [HttpPost("{id:guid}/force-suspend")]
    [ProducesResponseType(typeof(ProjectRuntime), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    [ProducesResponseType(409)]
    public Task<ActionResult<ProjectRuntime>> ForceSuspend(Guid id, CancellationToken ct)
        => TransitionAsync(id, RuntimeState.Suspending, "operator:force_suspend", ct);

    /// <summary>
    /// Operator escape hatch — park a runtime regardless of its current state. Where
    /// <see cref="ForceSuspend"/> only handles <see cref="RuntimeState.Online"/> (the
    /// "clean" Suspending edge), ForceStop additionally accepts the mid-boot states
    /// <see cref="RuntimeState.Booting"/> / <see cref="RuntimeState.Bootstrapping"/> /
    /// <see cref="RuntimeState.Waking"/> via the operator-override edges in
    /// <see cref="RuntimeStateMachine"/>. Used when a runtime is stuck partway through
    /// bootstrap (e.g. daemon contract mismatch leaves Bootstrapping looping forever)
    /// and "Stop" is unavailable because the runtime never reached Online.
    ///
    /// <para><b>Semantically identical to Stop once we land in Suspending</b> — the
    /// shared <see cref="TransitionAsync"/> helper still fires <c>Fly.StopMachine</c>
    /// and the webhook handler closes <c>Suspending → Suspended</c>. The only
    /// difference vs <see cref="ForceSuspend"/> is which source states are accepted.
    /// Returns 409 only for source states with no force-stop edge (Suspended, Pending,
    /// Crashed, Failed, Deleting, Deleted, Suspending itself).</para>
    /// </summary>
    [HttpPost("{id:guid}/force-stop")]
    [ProducesResponseType(typeof(ProjectRuntime), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    [ProducesResponseType(409)]
    public Task<ActionResult<ProjectRuntime>> ForceStop(Guid id, CancellationToken ct)
        => TransitionAsync(id, RuntimeState.Suspending, "operator:force_stop", ct);

    /// <summary>
    /// Move a runtime in any non-terminal state to <see cref="RuntimeState.Deleting"/>.
    /// The state graph allows Deleting from every live state, so this is the operator's
    /// "make it stop" button. Returns 409 only when the runtime is already
    /// <see cref="RuntimeState.Deleted"/> (a terminal state with no outgoing edges).
    /// </summary>
    [HttpPost("{id:guid}/force-delete")]
    [ProducesResponseType(typeof(ProjectRuntime), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    [ProducesResponseType(409)]
    public Task<ActionResult<ProjectRuntime>> ForceDelete(Guid id, CancellationToken ct)
        => TransitionAsync(id, RuntimeState.Deleting, "operator:force_delete", ct);

    /// <summary>
    /// Punch a live runtime into <see cref="RuntimeState.Crashed"/> so the existing
    /// crash-recovery chain (<c>ScheduleRespawnHandler</c> -> delayed
    /// <c>RespawnRuntimeJob</c>) tears it down and rebuilds it. The endpoint returns
    /// <b>202 Accepted</b> as soon as the transition + audit row are committed; the
    /// destroy-and-recreate happens asynchronously off the back of the domain event.
    ///
    /// <para><b>Why only some states.</b> The state graph (<see cref="RuntimeStateMachine"/>)
    /// only has direct edges into Crashed from <see cref="RuntimeState.Online"/>,
    /// <see cref="RuntimeState.Bootstrapping"/>, <see cref="RuntimeState.Waking"/>,
    /// <see cref="RuntimeState.Booting"/> and <see cref="RuntimeState.Suspending"/>.
    /// A pre-check rejects every other source state with a 409 plus a hint towards the
    /// right tool (e.g. reset-from-failed for Failed). Pre-checking gives the operator
    /// a clearer error than letting <see cref="ProjectRuntime.TransitionTo"/> bounce a
    /// generic "Illegal transition" message.</para>
    ///
    /// <para><b>Suspended.</b> Suspended has no direct -> Crashed edge — Fly already
    /// stopped the machine, "respawn" without a running machine to crash isn't
    /// meaningful. To rebuild a suspended runtime: wake it first, then force-respawn
    /// from Online. Out of scope for this endpoint.</para>
    /// </summary>
    [HttpPost("{id:guid}/force-respawn")]
    [ProducesResponseType(202)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    [ProducesResponseType(409)]
    public async Task<ActionResult> ForceRespawn(Guid id, CancellationToken ct)
    {
        var runtime = await _db.ProjectRuntimes.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (runtime is null)
        {
            return NotFound();
        }

        // Source states that have a legal direct edge to Crashed in RuntimeStateMachine.
        // Anything else is rejected up-front so the operator gets a state-aware hint
        // instead of the generic "Illegal transition" string from TransitionTo.
        if (!_forceRespawnableStates.Contains(runtime.State))
        {
            var hint = runtime.State == RuntimeState.Failed
                ? "Use reset-from-failed to move a Failed runtime back to Pending."
                : runtime.State == RuntimeState.Suspended
                    ? "Wake the runtime first, then force-respawn from Online."
                    : "Runtime is not in a respawnable state.";
            return Conflict(new
            {
                error = $"Cannot force-respawn runtime in state {runtime.State}. {hint}",
            });
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
        var triggeredBy = $"operator:{userId}";
        var fromState = runtime.State;

        var result = runtime.TransitionTo(
            RuntimeState.Crashed,
            "operator:force-respawn",
            triggeredBy,
            metadata: null);

        if (result.IsFailure)
        {
            // Defence-in-depth: the pre-check above should have caught every illegal
            // source state, but if the graph evolves and an entry slips through we
            // still want a clean 409 rather than a 500.
            _logger.LogWarning(
                "Operator {UserId} attempted illegal force-respawn on runtime {RuntimeId} (state={State}): {Error}",
                userId, runtime.Id, fromState, result.Error);
            return Conflict(new { error = result.Error });
        }

        await _db.SaveChangesAsync(ct);

        // ScheduleRespawnHandler is wired to RuntimeStateChanged where ToState=Crashed
        // and queues the delayed RespawnRuntimeJob from there. We don't wait for that
        // chain — Accepted() signals "we got it, the work is queued".
        _logger.LogInformation(
            "Operator {UserId} force-respawned runtime {RuntimeId} (was {State} -> Crashed)",
            userId, runtime.Id, fromState);

        return Accepted();
    }

    /// <summary>
    /// Source states from which a force-respawn is legal. Mirrors the direct edges
    /// into <see cref="RuntimeState.Crashed"/> declared in
    /// <see cref="RuntimeStateMachine"/>. Kept as a static field so the allocation
    /// doesn't repeat per request.
    /// </summary>
    private static readonly HashSet<RuntimeState> _forceRespawnableStates = new()
    {
        RuntimeState.Online,
        RuntimeState.Bootstrapping,
        RuntimeState.Waking,
        RuntimeState.Booting,
        RuntimeState.Suspending,
    };

    /// <summary>
    /// Shared body for the three operator transition endpoints. Loads the runtime,
    /// asks the entity to perform the transition (which validates against the state
    /// graph and raises the audit event), and either commits the change or returns the
    /// state-graph error as a 409. A failed transition leaves the runtime untouched —
    /// <see cref="ProjectRuntime.TransitionTo"/> bails before mutating state on
    /// rejection — so SaveChanges is only called on success.
    /// </summary>
    private async Task<ActionResult<ProjectRuntime>> TransitionAsync(
        Guid id,
        RuntimeState target,
        string reason,
        CancellationToken ct)
    {
        var runtime = await _db.ProjectRuntimes.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (runtime is null)
        {
            return NotFound();
        }

        // Tag the audit row with the operator's user id so post-mortems can answer
        // "who hit the big red button". Falls back to "operator:unknown" only when the
        // claim is somehow missing — the [Authorize] gate above should make that
        // unreachable in practice.
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
        var triggeredBy = $"operator:{userId}";

        var result = runtime.TransitionTo(target, reason, triggeredBy);
        if (result.IsFailure)
        {
            _logger.LogWarning(
                "Operator {UserId} attempted illegal transition on runtime {RuntimeId}: {Error}",
                userId, runtime.Id, result.Error);
            return Conflict(new { error = result.Error });
        }

        await _db.SaveChangesAsync(ct);

        // When the operator parks a runtime via force-suspend, the DB flip is
        // half the job — without the matching Fly StopMachine call the machine
        // keeps burning resources and the runtime sits in Suspending forever
        // (the exact drift the audit caught for ArchiveBranchHandler). Mirror
        // the IdlerJob.SuspendOne / ArchiveBranchHandler pattern: best-effort
        // call, log + swallow on transport error, RuntimeReconcilerJob retries
        // the stuck-Suspending case on its next tick.
        if (target == RuntimeState.Suspending && !string.IsNullOrEmpty(runtime.FlyMachineId))
        {
            try
            {
                await _fly.StopMachineAsync(
                    machineId: runtime.FlyMachineId,
                    options: null,
                    runtimeId: runtime.Id,
                    ct: ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "RuntimeAdmin force-suspend: Fly StopMachine call failed for machine {MachineId} (runtime {RuntimeId}); reconciler will retry.",
                    runtime.FlyMachineId, runtime.Id);
            }
        }

        _logger.LogInformation(
            "Operator {UserId} transitioned runtime {RuntimeId} -> {State} via {Reason}",
            userId, runtime.Id, target, reason);

        return Ok(runtime);
    }
}

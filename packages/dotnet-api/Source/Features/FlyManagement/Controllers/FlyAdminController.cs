using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Source.Features.FlyManagement.Configuration;
using Source.Features.FlyManagement.Models;
using Source.Infrastructure;
using Source.Infrastructure.AuthorizationModels;

namespace Source.Features.FlyManagement.Controllers;

/// <summary>
/// Operator-only HTTP surface for direct Fly.io resource inspection and manipulation.
/// Backs the admin UI / CLI tooling for support, debugging, and post-mortem cleanup —
/// none of these endpoints fit the normal "command/query through a runtime aggregate"
/// shape because the operator is reaching past our domain model to talk to Fly itself.
///
/// <para><b>Why no MediatR.</b> This is a pragmatic admin passthrough, not a business
/// feature. Every endpoint is a one-line forward to <see cref="FlyClient"/> (which
/// already writes <see cref="FlyOperation"/> audit rows for the side-effecting calls)
/// plus one paged read against that audit table. Wrapping the calls in commands would
/// add four files per endpoint without changing the behaviour — keep the slice thin
/// and let <see cref="FlyClient"/>'s existing audit pipeline carry the load.</para>
///
/// <para>Authorisation: <see cref="RoleConstants.SuperAdmin"/>, matching
/// <see cref="Source.Features.SystemSettings.Controllers.SystemSettingsController"/>.
/// These calls can destroy paid infrastructure — TenantAdmin is too broad.</para>
/// </summary>
[ApiController]
[Route("api/admin/fly")]
[Authorize(Roles = RoleConstants.SuperAdmin)]
[Tags("FlyAdmin")]
public class FlyAdminController : ControllerBase
{
    private readonly FlyClient _fly;
    private readonly ApplicationDbContext _db;
    private readonly IFlyOptionsAccessor _flyOptions;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FlyAdminController> _logger;

    public FlyAdminController(
        FlyClient fly,
        ApplicationDbContext db,
        IFlyOptionsAccessor flyOptions,
        IServiceScopeFactory scopeFactory,
        ILogger<FlyAdminController> logger)
    {
        _fly = fly;
        _db = db;
        _flyOptions = flyOptions;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    // ----------------------------------------------------------------------
    // Machines
    // ----------------------------------------------------------------------

    /// <summary>
    /// List every machine under the configured Fly app, enriched with our DB-side
    /// linkage (which <see cref="Source.Features.RuntimeLifecycle.Models.ProjectRuntime"/>,
    /// project, and branch each machine maps to). Drives the super-admin "Fly cleanup"
    /// page where the operator wants to spot orphans — Fly machines lingering with no
    /// live runtime row, still billing.
    ///
    /// <para><b>Linkage.</b> <c>ProjectRuntime.FlyMachineId</c> is indexed; one
    /// <c>WHERE FlyMachineId IN (...)</c> over the returned set resolves every link.
    /// Soft-deleted runtimes fall out of the default query filter and surface as
    /// orphans — intentional, because their Fly resources are exactly what the
    /// cleanup page exists to evict.</para>
    /// </summary>
    [HttpGet("machines")]
    [ProducesResponseType(typeof(List<FlyMachineAdminRow>), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<List<FlyMachineAdminRow>>> ListMachines(CancellationToken ct)
    {
        var machines = await _fly.ListMachinesAsync(ct);

        if (machines.Count == 0)
        {
            return Ok(new List<FlyMachineAdminRow>());
        }

        // Resolve linkage in a single query: pull every runtime whose FlyMachineId
        // matches any id Fly returned, plus its Project and Branch names for display.
        // Project a flat DTO so EF stays happy and we don't pull the full ProjectRuntime
        // graph just to grab two display strings.
        var machineIds = machines.Select(m => m.Id).ToList();
        var links = await _db.ProjectRuntimes
            .Where(r => r.FlyMachineId != null && machineIds.Contains(r.FlyMachineId))
            .Select(r => new
            {
                FlyMachineId = r.FlyMachineId!,
                RuntimeId = r.Id,
                r.ProjectId,
                r.BranchId,
                ProjectName = r.Project.Name,
                BranchName = r.Branch.Name,
            })
            .ToListAsync(ct);

        var linkByMachineId = links.ToDictionary(l => l.FlyMachineId);

        var rows = machines.Select(m =>
        {
            linkByMachineId.TryGetValue(m.Id, out var link);
            return new FlyMachineAdminRow(
                Id: m.Id,
                Name: m.Name,
                State: m.State,
                Region: m.Region,
                InstanceId: m.InstanceId,
                PrivateIp: m.PrivateIp,
                CreatedAt: m.CreatedAt,
                LinkedRuntimeId: link?.RuntimeId,
                LinkedProjectId: link?.ProjectId,
                LinkedBranchId: link?.BranchId,
                LinkedProjectName: link?.ProjectName,
                LinkedBranchName: link?.BranchName,
                IsOrphan: link is null);
        }).ToList();

        return Ok(rows);
    }

    /// <summary>Fetch the current state of a single machine by Fly machine id.</summary>
    [HttpGet("machines/{id}")]
    [ProducesResponseType(typeof(FlyMachine), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<FlyMachine>> GetMachine(string id, CancellationToken ct)
        => Ok(await _fly.GetMachineAsync(id, ct));

    /// <summary>Transition a stopped or suspended machine back to <c>started</c>.</summary>
    [HttpPost("machines/{id}/start")]
    [ProducesResponseType(204)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult> StartMachine(string id, CancellationToken ct)
    {
        await _fly.StartMachineAsync(id, ct: ct);
        return NoContent();
    }

    /// <summary>
    /// Stop a running machine. Optional body lets the operator override the signal
    /// or grace period; omit for Fly's defaults (graceful SIGINT then SIGKILL).
    /// </summary>
    [HttpPost("machines/{id}/stop")]
    [ProducesResponseType(204)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult> StopMachine(
        string id,
        [FromBody] StopMachineRequest? options,
        CancellationToken ct)
    {
        await _fly.StopMachineAsync(id, options, ct: ct);
        return NoContent();
    }

    /// <summary>
    /// Suspend a machine (preserves in-memory state for faster restart). Only valid
    /// on machines that opted in at config time; Fly returns 422 otherwise.
    /// </summary>
    [HttpPost("machines/{id}/suspend")]
    [ProducesResponseType(204)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult> SuspendMachine(string id, CancellationToken ct)
    {
        await _fly.SuspendMachineAsync(id, ct: ct);
        return NoContent();
    }

    /// <summary>
    /// Destroy a machine. <paramref name="force"/> = <c>true</c> instructs Fly to skip
    /// the graceful stop and tear down a stuck VM. Use sparingly — billing stops at
    /// destroy, but state is gone.
    /// </summary>
    [HttpDelete("machines/{id}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult> DestroyMachine(
        string id,
        [FromQuery] bool force,
        CancellationToken ct)
    {
        await _fly.DestroyMachineAsync(id, force, ct: ct);
        return NoContent();
    }

    /// <summary>
    /// Destroy many machines in one request. Backs the super-admin "Fly cleanup" page
    /// where the operator multi-selects rows from <see cref="ListMachines"/> and tears
    /// them all down.
    ///
    /// <para><b>Capacity.</b> Hard-capped at <see cref="BulkDestroyMaxIds"/> ids per
    /// request (400 if exceeded) — keeps a UI typo from blowing through Fly's API quota
    /// and bounds the worst-case latency. Empty / null id lists also 400.</para>
    ///
    /// <para><b>Concurrency.</b> Up to <see cref="BulkDestroyConcurrency"/> destroys run
    /// in parallel behind a <see cref="SemaphoreSlim"/> gate — fast enough that 100 ids
    /// finish well inside the request budget, slow enough that we don't pummel Fly with
    /// a synchronous burst. Each parallel task resolves its own <see cref="FlyClient"/>
    /// from a fresh <see cref="IServiceScope"/> so the non-thread-safe
    /// <see cref="ApplicationDbContext"/> isn't shared across destroys (see
    /// <see cref="BulkDestroyAsync"/> for the full reasoning).</para>
    ///
    /// <para><b>Failure isolation.</b> Per-item exceptions (<see cref="FlyApiException"/>
    /// for Fly-side rejections, anything else for transport failures) are caught and
    /// recorded in the response's <see cref="BulkDestroyResponse.Failed"/> list — one
    /// stuck VM never fails the rest of the batch. The cancellation token still aborts
    /// the whole batch on client disconnect.</para>
    ///
    /// <para><b>Audit.</b> Every destroy still routes through
    /// <see cref="FlyClient.DestroyMachineAsync"/>, so one <see cref="FlyOperation"/> row
    /// per item lands in the audit table — the existing
    /// <c>/api/admin/fly/operations</c> view is the source of truth for the per-item
    /// transcript; the response body here is just the inline summary.</para>
    /// </summary>
    [HttpPost("machines/bulk-destroy")]
    [ProducesResponseType(typeof(BulkDestroyResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public Task<ActionResult<BulkDestroyResponse>> BulkDestroyMachines(
        [FromBody] BulkDestroyRequest body,
        CancellationToken ct)
        => BulkDestroyAsync(
            body,
            destroyOne: (fly, id, token) => fly.DestroyMachineAsync(id, body.Force, ct: token),
            ct);

    // ----------------------------------------------------------------------
    // Volumes
    // ----------------------------------------------------------------------

    /// <summary>
    /// List every persistent volume under the configured Fly app, enriched with our
    /// DB-side linkage (twin of <see cref="ListMachines"/> for volumes). Detached
    /// volumes outlive their machines and keep billing storage, so orphan detection
    /// here is at least as valuable as on the machine side.
    /// </summary>
    [HttpGet("volumes")]
    [ProducesResponseType(typeof(List<FlyVolumeAdminRow>), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<List<FlyVolumeAdminRow>>> ListVolumes(CancellationToken ct)
    {
        var volumes = await _fly.ListVolumesAsync(ct);

        if (volumes.Count == 0)
        {
            return Ok(new List<FlyVolumeAdminRow>());
        }

        var volumeIds = volumes.Select(v => v.Id).ToList();
        var links = await _db.ProjectRuntimes
            .Where(r => r.FlyVolumeId != null && volumeIds.Contains(r.FlyVolumeId))
            .Select(r => new
            {
                FlyVolumeId = r.FlyVolumeId!,
                RuntimeId = r.Id,
                r.ProjectId,
                r.BranchId,
                ProjectName = r.Project.Name,
                BranchName = r.Branch.Name,
            })
            .ToListAsync(ct);

        var linkByVolumeId = links.ToDictionary(l => l.FlyVolumeId);

        var rows = volumes.Select(v =>
        {
            linkByVolumeId.TryGetValue(v.Id, out var link);
            return new FlyVolumeAdminRow(
                Id: v.Id,
                Name: v.Name,
                Region: v.Region,
                SizeGb: v.SizeGb,
                State: v.State,
                AttachedMachineId: v.AttachedMachineId,
                Encrypted: v.Encrypted,
                CreatedAt: v.CreatedAt,
                LinkedRuntimeId: link?.RuntimeId,
                LinkedProjectId: link?.ProjectId,
                LinkedBranchId: link?.BranchId,
                LinkedProjectName: link?.ProjectName,
                LinkedBranchName: link?.BranchName,
                IsOrphan: link is null);
        }).ToList();

        return Ok(rows);
    }

    /// <summary>Destroy a volume. Irreversible — data is gone.</summary>
    [HttpDelete("volumes/{id}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult> DestroyVolume(string id, CancellationToken ct)
    {
        await _fly.DestroyVolumeAsync(id, ct: ct);
        return NoContent();
    }

    /// <summary>
    /// Destroy many volumes in one request. Twin of <see cref="BulkDestroyMachines"/> —
    /// same cap, same concurrency, same failure-isolation contract. The UI calls
    /// machines-bulk-destroy first and only invokes this after that succeeds (Fly
    /// refuses to destroy a volume still attached to a machine, so the order matters
    /// across resources but is the caller's job, not this endpoint's).
    ///
    /// <para><b>Note on <see cref="BulkDestroyRequest.Force"/>.</b> Volumes don't have a
    /// force flag on Fly's side — <see cref="FlyClient.DestroyVolumeAsync"/> ignores it.
    /// We accept it on the request body for shape-symmetry with the machines endpoint
    /// (same DTO) and silently drop it here.</para>
    /// </summary>
    [HttpPost("volumes/bulk-destroy")]
    [ProducesResponseType(typeof(BulkDestroyResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public Task<ActionResult<BulkDestroyResponse>> BulkDestroyVolumes(
        [FromBody] BulkDestroyRequest body,
        CancellationToken ct)
        => BulkDestroyAsync(
            body,
            destroyOne: (fly, id, token) => fly.DestroyVolumeAsync(id, ct: token),
            ct);

    /// <summary>
    /// Grow a volume online. Backs Scene 4 of the runtime-volume-cache spec: when a
    /// runtime hits 80/90/95 % full the daemon emits a disk-pressure event, the UI shows
    /// "Upgrade to N GB?" and approval lands here. Fly rejects shrinks server-side, so
    /// we only guard against the trivial mistakes (zero / negative size) — anything else
    /// (size below current, region mismatch, app missing) becomes a 5xx via the
    /// <see cref="FlyApiException"/> that bubbles out of <see cref="FlyClient"/>.
    ///
    /// <para>Daemon signalling (the <c>disk_capacity_changed</c> SignalR event) is
    /// deferred to the SignalR / daemon-architecture specs; this endpoint is the API
    /// half of the loop.</para>
    /// </summary>
    [HttpPost("volumes/{id}/extend")]
    [ProducesResponseType(typeof(FlyVolume), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<FlyVolume>> ExtendVolume(
        string id,
        [FromBody] ExtendVolumeRequest body,
        CancellationToken ct)
    {
        if (body.SizeGb <= 0)
        {
            return BadRequest(new { error = "sizeGb must be positive" });
        }

        var volume = await _fly.ExtendVolumeAsync(id, body.SizeGb, ct: ct);
        return Ok(volume);
    }

    // ----------------------------------------------------------------------
    // App
    // ----------------------------------------------------------------------

    /// <summary>Read the configured Fly app's metadata.</summary>
    [HttpGet("app")]
    [ProducesResponseType(typeof(FlyApp), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<FlyApp>> GetApp(CancellationToken ct)
        => Ok(await _fly.GetAppAsync(ct));

    /// <summary>Idempotent app bootstrap — creates the app on the configured org if missing.</summary>
    [HttpPost("app/ensure")]
    [ProducesResponseType(typeof(FlyApp), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<FlyApp>> EnsureApp(CancellationToken ct)
        => Ok(await _fly.EnsureAppAsync(ct));

    // ----------------------------------------------------------------------
    // Connection probe
    // ----------------------------------------------------------------------

    /// <summary>
    /// Probe the configured Fly credentials. Reads <c>Fly:*</c> SystemSettings, reports
    /// presence of every key, and exercises the PAT + app name via
    /// <see cref="FlyClient.PingAsync"/> (which GETs <c>/apps/{AppName}</c> and treats
    /// 200 or 404 as "auth + transport OK").
    ///
    /// <para>Always returns 200 — auth failures are reported in the body so the UI can
    /// render a structured checklist. Note: <see cref="FlyClient.PingAsync"/> writes a
    /// <see cref="FlyOperation"/> audit row via the standard send pipeline; that's
    /// expected and informative for post-mortems, not a leak.</para>
    /// </summary>
    [HttpPost("test-connection")]
    [ProducesResponseType(typeof(FlyTestConnectionResponse), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<FlyTestConnectionResponse>> TestConnection(CancellationToken ct)
    {
        var options = _flyOptions.Current;

        var apiTokenSet = !string.IsNullOrWhiteSpace(options.ApiToken);
        var appNameSet = !string.IsNullOrWhiteSpace(options.AppName);
        var orgSlugSet = !string.IsNullOrWhiteSpace(options.OrgSlug);

        var pingSucceeded = false;
        string? pingError = null;

        if (apiTokenSet && appNameSet)
        {
            try
            {
                pingSucceeded = await _fly.PingAsync(ct);
                if (!pingSucceeded)
                {
                    // PingAsync swallows transport exceptions and returns false; we
                    // can't tell the operator whether it was a 401 vs DNS without
                    // re-implementing the call. Surface a generic but accurate message.
                    pingError = "Fly API rejected the request or was unreachable (token invalid, app missing, or network error)";
                }
            }
            catch (Exception ex)
            {
                pingError = ex.Message;
                _logger.LogWarning(ex, "Fly test-connection: PingAsync threw");
            }
        }

        var isValid = apiTokenSet && appNameSet && pingSucceeded;

        string message;
        if (isValid)
        {
            message = $"Connected to Fly app: {options.AppName}";
        }
        else
        {
            var missing = new List<string>();
            if (!apiTokenSet) missing.Add("ApiToken");
            if (!appNameSet) missing.Add("AppName");
            if (!orgSlugSet) missing.Add("OrgSlug");

            if (missing.Count > 0)
            {
                message = $"Configuration incomplete: missing {string.Join(", ", missing)}";
            }
            else if (!pingSucceeded)
            {
                message = $"Fly rejected the credentials: {pingError ?? "unknown error"}";
            }
            else
            {
                message = "Configuration incomplete";
            }
        }

        return Ok(new FlyTestConnectionResponse(
            ApiTokenSet: apiTokenSet,
            AppNameSet: appNameSet,
            OrgSlugSet: orgSlugSet,
            PingSucceeded: pingSucceeded,
            PingError: pingError,
            AppExists: pingSucceeded,
            AppName: appNameSet ? options.AppName : null,
            IsValid: isValid,
            Message: message));
    }

    // ----------------------------------------------------------------------
    // Operations (audit log)
    // ----------------------------------------------------------------------

    /// <summary>
    /// Page through the <see cref="FlyOperation"/> audit log. Filters compose with AND.
    /// <paramref name="status"/> matches the <see cref="FlyOperationStatus"/> enum
    /// case-insensitively (<c>pending</c>, <c>succeeded</c>, <c>failed</c>); unknown
    /// values are silently ignored so a typo doesn't 400 the operator.
    /// <paramref name="pageSize"/> is hard-capped at 200 — bigger pages just waste
    /// memory and risk timing out the request.
    /// </summary>
    [HttpGet("operations")]
    [ProducesResponseType(typeof(FlyOperationsResponse), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<FlyOperationsResponse>> ListOperations(
        [FromQuery] string? status,
        [FromQuery] DateTime? since,
        [FromQuery] Guid? runtimeId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        // Defensive defaults — negative page numbers and zero/negative page sizes are
        // pure user error; coerce rather than 400. 200 is the cap (~50KB per row × 200
        // = manageable JSON payload even with full request/response bodies inlined).
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 50;
        pageSize = Math.Min(pageSize, 200);

        var query = _db.FlyOperations.AsQueryable();

        if (!string.IsNullOrEmpty(status)
            && Enum.TryParse<FlyOperationStatus>(status, ignoreCase: true, out var parsed))
        {
            query = query.Where(o => o.Status == parsed);
        }

        if (since.HasValue)
        {
            query = query.Where(o => o.CreatedAt >= since.Value);
        }

        if (runtimeId.HasValue)
        {
            query = query.Where(o => o.RuntimeId == runtimeId.Value);
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(o => o.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        _logger.LogInformation(
            "Admin listed FlyOperations (page={Page}, pageSize={PageSize}, count={Count}, total={Total})",
            page, pageSize, items.Count, total);

        return Ok(new FlyOperationsResponse(items, total, page, pageSize));
    }

    // ----------------------------------------------------------------------
    // Bulk destroy plumbing
    // ----------------------------------------------------------------------

    /// <summary>
    /// Hard cap on ids per bulk-destroy request. Caps the worst-case latency and Fly
    /// API spend on a UI typo / runaway "select all". 100 is comfortably above
    /// realistic cleanup batches (a Fly app rarely has more than a few dozen leaked
    /// resources) and well below where a synchronous HTTP request becomes a problem.
    /// </summary>
    private const int BulkDestroyMaxIds = 100;

    /// <summary>
    /// Maximum concurrent Fly destroy calls inside one bulk request. 5 gives the
    /// batch a useful speedup over serial without saturating Fly's API.
    ///
    /// <para><b>Why this is safe with EF Core.</b> Each parallel destroy runs inside its
    /// own <see cref="IServiceScope"/> (see <see cref="BulkDestroyAsync"/>), so every task
    /// gets a fresh <see cref="ApplicationDbContext"/> + <see cref="FlyClient"/>. The
    /// per-item <see cref="FlyOperation"/> audit row is written on its own context — no
    /// sharing, no "second operation started on this context instance" race. If we ever
    /// reverted to the request-scoped DbContext, this constant would have to drop to 1.</para>
    /// </summary>
    private const int BulkDestroyConcurrency = 5;

    /// <summary>
    /// Shared body for <see cref="BulkDestroyMachines"/> and <see cref="BulkDestroyVolumes"/>:
    /// validates the request, gates destroys behind a <see cref="SemaphoreSlim"/>, isolates
    /// per-item failures, and rolls up the counts. The <paramref name="destroyOne"/>
    /// delegate is the only resource-specific bit — receives a per-task
    /// <see cref="FlyClient"/> (resolved from a fresh DI scope) and the per-item id, and
    /// lets this helper stay shape-agnostic.
    ///
    /// <para><b>Per-task scope.</b> <see cref="ApplicationDbContext"/> is registered scoped
    /// and is NOT thread-safe; <see cref="FlyClient"/> takes the scoped context via its
    /// constructor and writes a <see cref="FlyOperation"/> audit row on every call. Fanning
    /// out concurrent destroys on the controller's request-scoped <see cref="FlyClient"/>
    /// would trip EF Core's "a second operation was started on this context instance"
    /// guard and corrupt the context for the rest of the batch. We sidestep that by giving
    /// each parallel task its own <see cref="IServiceScope"/> via
    /// <see cref="IServiceScopeFactory"/> — fresh DbContext, fresh FlyClient, no shared
    /// state across tasks. This is the canonical EF Core pattern for in-request fan-out.</para>
    /// </summary>
    private async Task<ActionResult<BulkDestroyResponse>> BulkDestroyAsync(
        BulkDestroyRequest body,
        Func<FlyClient, string, CancellationToken, Task> destroyOne,
        CancellationToken ct)
    {
        if (body?.Ids is null || body.Ids.Count == 0)
        {
            return BadRequest(new { error = "ids must contain at least one id" });
        }

        if (body.Ids.Count > BulkDestroyMaxIds)
        {
            return BadRequest(new
            {
                error = $"bulk-destroy accepts at most {BulkDestroyMaxIds} ids per request (got {body.Ids.Count})",
            });
        }

        // Dedupe defensively — the UI shouldn't send dupes but a refresh race could.
        // Preserve input order so the response's Failed list reads in the same order
        // the operator selected.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ids = body.Ids
            .Where(id => !string.IsNullOrWhiteSpace(id) && seen.Add(id))
            .ToList();

        var failed = new List<BulkDestroyFailure>();
        var failedLock = new object();
        var succeeded = 0;

        using var gate = new SemaphoreSlim(BulkDestroyConcurrency, BulkDestroyConcurrency);

        var tasks = ids.Select(async id =>
        {
            await gate.WaitAsync(ct);
            try
            {
                // Fresh DI scope per task — gives this destroy its own ApplicationDbContext
                // and FlyClient so concurrent items don't share EF Core's non-thread-safe
                // change tracker. See the type-doc on BulkDestroyAsync for why this matters.
                using var scope = _scopeFactory.CreateScope();
                var fly = scope.ServiceProvider.GetRequiredService<FlyClient>();
                await destroyOne(fly, id, ct);
                Interlocked.Increment(ref succeeded);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Caller hung up — let the cancellation propagate out of WhenAll.
                // Don't record this as a per-item failure; the whole batch is dead.
                throw;
            }
            catch (FlyApiException ex)
            {
                lock (failedLock)
                {
                    failed.Add(new BulkDestroyFailure(id, ex.Message));
                }
                _logger.LogWarning(ex,
                    "Bulk destroy item failed (id={Id}): {Message}", id, ex.Message);
            }
            catch (Exception ex)
            {
                lock (failedLock)
                {
                    failed.Add(new BulkDestroyFailure(id, ex.Message));
                }
                _logger.LogWarning(ex,
                    "Bulk destroy item threw (id={Id}): {Message}", id, ex.Message);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);

        _logger.LogInformation(
            "Bulk destroy completed: requested={Requested}, succeeded={Succeeded}, failed={Failed}",
            ids.Count, succeeded, failed.Count);

        return Ok(new BulkDestroyResponse(
            Requested: ids.Count,
            Succeeded: succeeded,
            Failed: failed));
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Source.Features.BoxManagement.Configuration;
using Source.Features.BoxManagement.Models;
using Source.Infrastructure;
using Source.Infrastructure.AuthorizationModels;

namespace Source.Features.BoxManagement.Controllers;

/// <summary>
/// Operator-only HTTP surface for direct Box resource inspection and manipulation.
/// Backs the super-admin "Box cleanup" page plus support / debugging / post-mortem
/// tooling — none of these endpoints fit the normal "command/query through a runtime
/// aggregate" shape because the operator is reaching past our domain model to talk
/// to Box itself.
///
/// <para><b>Why no MediatR.</b> This is a pragmatic admin passthrough, not a business
/// feature. Every endpoint is a one-line forward to <see cref="BoxClient"/> (which
/// already writes <see cref="BoxOperation"/> audit rows for the side-effecting calls)
/// plus one paged read against that audit table.</para>
///
/// <para>Authorisation: <see cref="RoleConstants.SuperAdmin"/> — these calls can
/// permanently destroy user disks; TenantAdmin is too broad.</para>
/// </summary>
[ApiController]
[Route("api/admin/box")]
[Authorize(Roles = RoleConstants.SuperAdmin)]
[Tags("BoxAdmin")]
public class BoxAdminController : ControllerBase
{
    private readonly BoxClient _box;
    private readonly ApplicationDbContext _db;
    private readonly IBoxOptionsAccessor _boxOptions;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BoxAdminController> _logger;

    public BoxAdminController(
        BoxClient box,
        ApplicationDbContext db,
        IBoxOptionsAccessor boxOptions,
        IServiceScopeFactory scopeFactory,
        ILogger<BoxAdminController> logger)
    {
        _box = box;
        _db = db;
        _boxOptions = boxOptions;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    // ----------------------------------------------------------------------
    // Boxes
    // ----------------------------------------------------------------------

    /// <summary>
    /// List every box on the account, enriched with our DB-side linkage (which
    /// <see cref="Source.Features.RuntimeLifecycle.Models.ProjectRuntime"/>, project,
    /// and branch each box maps to) and a template flag. Drives the Box cleanup page
    /// where the operator spots orphans — boxes lingering with no live runtime row.
    ///
    /// <para><b>Linkage.</b> <c>ProjectRuntime.BoxId</c> is indexed; one
    /// <c>WHERE BoxId IN (...)</c> over the returned set resolves every link.
    /// Soft-deleted runtimes fall out of the default query filter and surface as
    /// orphans — intentional, because their boxes are exactly what the cleanup page
    /// exists to evict. Registered template boxes are flagged (never orphans) so a
    /// hasty "select all" can't delete the golden template.</para>
    /// </summary>
    [HttpGet("boxes")]
    [ProducesResponseType(typeof(List<BoxAdminRow>), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<List<BoxAdminRow>>> ListBoxes(CancellationToken ct)
    {
        var boxes = await _box.ListBoxesAsync(ct);

        if (boxes.Count == 0)
        {
            return Ok(new List<BoxAdminRow>());
        }

        var boxIds = boxes.Select(b => b.Id).ToList();
        var links = await _db.ProjectRuntimes
            .Where(r => r.BoxId != null && boxIds.Contains(r.BoxId))
            .Select(r => new
            {
                BoxId = r.BoxId!,
                RuntimeId = r.Id,
                r.ProjectId,
                r.BranchId,
                ProjectName = r.Project.Name,
                BranchName = r.Branch.Name,
            })
            .ToListAsync(ct);
        var linkByBoxId = links.ToDictionary(l => l.BoxId);

        var templateBoxIds = await _db.RuntimeTemplates
            .Select(t => t.BoxId)
            .ToListAsync(ct);
        var templateSet = new HashSet<string>(templateBoxIds, StringComparer.Ordinal);

        var rows = boxes.Select(b =>
        {
            linkByBoxId.TryGetValue(b.Id, out var link);
            var isTemplate = templateSet.Contains(b.Id);
            return new BoxAdminRow(
                Id: b.Id,
                Name: b.Name,
                Status: b.State,
                Size: b.Type,
                Region: b.Region,
                TtlSeconds: b.TtlSeconds,
                CreatedAt: b.CreatedAt,
                LinkedRuntimeId: link?.RuntimeId,
                LinkedProjectId: link?.ProjectId,
                LinkedBranchId: link?.BranchId,
                LinkedProjectName: link?.ProjectName,
                LinkedBranchName: link?.BranchName,
                IsTemplate: isTemplate,
                IsOrphan: link is null && !isTemplate);
        }).ToList();

        return Ok(rows);
    }

    /// <summary>Fetch the current state of a single box by Box id.</summary>
    [HttpGet("boxes/{id}")]
    [ProducesResponseType(typeof(BoxVm), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<BoxVm>> GetBox(string id, CancellationToken ct)
        => Ok(await _box.GetBoxAsync(id, ct));

    /// <summary>Resume an archived box (counts one machine start against the account budget).</summary>
    [HttpPost("boxes/{id}/resume")]
    [ProducesResponseType(204)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult> ResumeBox(string id, CancellationToken ct)
    {
        await _box.ResumeBoxAsync(id, ct: ct);
        return NoContent();
    }

    /// <summary>Stop a running box — archives it with a fresh snapshot; billing pauses.</summary>
    [HttpPost("boxes/{id}/stop")]
    [ProducesResponseType(204)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult> StopBox(string id, CancellationToken ct)
    {
        await _box.StopBoxAsync(id, ct: ct);
        return NoContent();
    }

    /// <summary>
    /// Permanently delete a box AND its snapshots. Irreversible — data is gone.
    /// Registered template boxes are refused (409) so the golden template can't be
    /// fat-fingered away; yank the template registration first if you really mean it.
    /// </summary>
    [HttpDelete("boxes/{id}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(409)]
    public async Task<ActionResult> DeleteBox(string id, CancellationToken ct)
    {
        var isTemplate = await _db.RuntimeTemplates.AnyAsync(t => t.BoxId == id, ct);
        if (isTemplate)
        {
            return Conflict(new { error = "Box is a registered runtime template. Yank the template first." });
        }

        await _box.DeleteBoxAsync(id, ct: ct);
        return NoContent();
    }

    /// <summary>
    /// Delete many boxes in one request. Backs the Box cleanup page's multi-select.
    /// Hard-capped at 100 ids; up to 5 deletes run in parallel, each on its own DI
    /// scope (fresh DbContext + BoxClient) so EF Core's non-thread-safe context is
    /// never shared. Registered templates are skipped and reported as failures.
    /// </summary>
    [HttpPost("boxes/bulk-delete")]
    [ProducesResponseType(typeof(BulkDeleteResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<BulkDeleteResponse>> BulkDeleteBoxes(
        [FromBody] BulkDeleteRequest body,
        CancellationToken ct)
    {
        var templateBoxIds = await _db.RuntimeTemplates
            .Select(t => t.BoxId)
            .ToListAsync(ct);
        var templateSet = new HashSet<string>(templateBoxIds, StringComparer.Ordinal);

        return await BulkDeleteAsync(
            body,
            deleteOne: (box, id, token) =>
            {
                if (templateSet.Contains(id))
                {
                    throw new InvalidOperationException("Box is a registered runtime template.");
                }
                return box.DeleteBoxAsync(id, ct: token);
            },
            ct);
    }

    // ----------------------------------------------------------------------
    // Snapshots
    // ----------------------------------------------------------------------

    /// <summary>
    /// List the snapshots of every box on the account, flagged as orphan when the
    /// owning box maps to no live runtime and no registered template. Twin of
    /// <see cref="ListBoxes"/>.
    ///
    /// <para>Per the OpenAPI contract snapshots are a PER-BOX resource
    /// (<c>GET /boxes/{boxId}/snapshots</c> — there is no account-level listing),
    /// so this aggregates over the box list, isolating per-box failures. There is
    /// also no snapshot-delete endpoint: snapshots go away with their box
    /// (<c>DELETE /boxes/{id}</c> removes the box AND its snapshots), so the old
    /// snapshot delete endpoints are gone.</para>
    /// </summary>
    [HttpGet("snapshots")]
    [ProducesResponseType(typeof(List<BoxSnapshotAdminRow>), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<List<BoxSnapshotAdminRow>>> ListSnapshots(CancellationToken ct)
    {
        var boxes = await _box.ListBoxesAsync(ct);
        if (boxes.Count == 0)
        {
            return Ok(new List<BoxSnapshotAdminRow>());
        }

        var boxIds = boxes.Select(b => b.Id).ToList();

        var links = await _db.ProjectRuntimes
            .Where(r => r.BoxId != null && boxIds.Contains(r.BoxId))
            .Select(r => new { BoxId = r.BoxId!, RuntimeId = r.Id })
            .ToListAsync(ct);
        var runtimeByBoxId = links.ToDictionary(l => l.BoxId, l => l.RuntimeId);

        var templateBoxIds = await _db.RuntimeTemplates
            .Select(t => t.BoxId)
            .ToListAsync(ct);
        var templateSet = new HashSet<string>(templateBoxIds, StringComparer.Ordinal);

        var rows = new List<BoxSnapshotAdminRow>();
        foreach (var box in boxes)
        {
            List<BoxSnapshot> snapshots;
            try
            {
                snapshots = await _box.ListSnapshotsAsync(box.Id, ct);
            }
            catch (BoxApiException ex)
            {
                // One sick box must not blank the whole cleanup page.
                _logger.LogWarning(ex,
                    "Box admin: listing snapshots for box {BoxId} failed ({Code}); skipping",
                    box.Id, ex.ErrorCode);
                continue;
            }

            Guid? runtimeId = runtimeByBoxId.TryGetValue(box.Id, out var rid) ? rid : null;
            var isTemplateBox = templateSet.Contains(box.Id);

            rows.AddRange(snapshots.Select(s => new BoxSnapshotAdminRow(
                Id: s.Id,
                BoxId: box.Id,
                SizeBytes: s.SizeBytes,
                CreatedAt: s.CreatedAt,
                LinkedRuntimeId: runtimeId,
                IsOrphan: runtimeId is null && !isTemplateBox)));
        }

        return Ok(rows);
    }

    // ----------------------------------------------------------------------
    // Connection probe
    // ----------------------------------------------------------------------

    /// <summary>
    /// Probe the configured Box credentials. Reads <c>Box:*</c> SystemSettings,
    /// reports presence of the API key, and exercises it via
    /// <see cref="BoxClient.PingAsync"/> (GET <c>/me</c>).
    ///
    /// <para>Always returns 200 — auth failures are reported in the body so the UI
    /// can render a structured checklist.</para>
    /// </summary>
    [HttpPost("test-connection")]
    [ProducesResponseType(typeof(BoxTestConnectionResponse), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<BoxTestConnectionResponse>> TestConnection(CancellationToken ct)
    {
        var options = _boxOptions.Current;
        var apiKeySet = !string.IsNullOrWhiteSpace(options.ApiKey);

        var pingSucceeded = false;
        string? pingError = null;

        if (apiKeySet)
        {
            try
            {
                pingSucceeded = await _box.PingAsync(ct);
                if (!pingSucceeded)
                {
                    pingError = "Box API rejected the request or was unreachable (key invalid or network error)";
                }
            }
            catch (Exception ex)
            {
                pingError = ex.Message;
                _logger.LogWarning(ex, "Box test-connection: PingAsync threw");
            }
        }

        var isValid = apiKeySet && pingSucceeded;
        var message = isValid
            ? "Connected to the Box API."
            : !apiKeySet
                ? "Configuration incomplete: missing ApiKey"
                : $"Box rejected the credentials: {pingError ?? "unknown error"}";

        return Ok(new BoxTestConnectionResponse(
            ApiKeySet: apiKeySet,
            PingSucceeded: pingSucceeded,
            PingError: pingError,
            IsValid: isValid,
            Message: message));
    }

    // ----------------------------------------------------------------------
    // Operations (audit log)
    // ----------------------------------------------------------------------

    /// <summary>
    /// Page through the <see cref="BoxOperation"/> audit log. Filters compose with AND.
    /// <paramref name="status"/> matches the <see cref="BoxOperationStatus"/> enum
    /// case-insensitively; unknown values are silently ignored so a typo doesn't 400
    /// the operator. <paramref name="pageSize"/> is hard-capped at 200.
    /// </summary>
    [HttpGet("operations")]
    [ProducesResponseType(typeof(BoxOperationsResponse), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<BoxOperationsResponse>> ListOperations(
        [FromQuery] string? status,
        [FromQuery] DateTime? since,
        [FromQuery] Guid? runtimeId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 50;
        pageSize = Math.Min(pageSize, 200);

        var query = _db.BoxOperations.AsQueryable();

        if (!string.IsNullOrEmpty(status)
            && Enum.TryParse<BoxOperationStatus>(status, ignoreCase: true, out var parsed))
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
            "Admin listed BoxOperations (page={Page}, pageSize={PageSize}, count={Count}, total={Total})",
            page, pageSize, items.Count, total);

        return Ok(new BoxOperationsResponse(items, total, page, pageSize));
    }

    // ----------------------------------------------------------------------
    // Bulk delete plumbing
    // ----------------------------------------------------------------------

    /// <summary>Hard cap on ids per bulk-delete request — bounds latency and API spend on a UI typo.</summary>
    private const int BulkDeleteMaxIds = 100;

    /// <summary>
    /// Maximum concurrent Box delete calls inside one bulk request. Each parallel
    /// task runs in its own <see cref="IServiceScope"/> (fresh
    /// <see cref="ApplicationDbContext"/> + <see cref="BoxClient"/>) so the
    /// non-thread-safe EF context is never shared — the canonical EF Core pattern
    /// for in-request fan-out. Drop to 1 if this ever reverts to the request scope.
    /// </summary>
    private const int BulkDeleteConcurrency = 5;

    /// <summary>
    /// Shared body for the two bulk-delete endpoints: validates the request, gates
    /// deletes behind a <see cref="SemaphoreSlim"/>, isolates per-item failures, and
    /// rolls up the counts. The <paramref name="deleteOne"/> delegate is the only
    /// resource-specific bit.
    /// </summary>
    private async Task<ActionResult<BulkDeleteResponse>> BulkDeleteAsync(
        BulkDeleteRequest body,
        Func<BoxClient, string, CancellationToken, Task> deleteOne,
        CancellationToken ct)
    {
        if (body?.Ids is null || body.Ids.Count == 0)
        {
            return BadRequest(new { error = "ids must contain at least one id" });
        }

        if (body.Ids.Count > BulkDeleteMaxIds)
        {
            return BadRequest(new
            {
                error = $"bulk-delete accepts at most {BulkDeleteMaxIds} ids per request (got {body.Ids.Count})",
            });
        }

        // Dedupe defensively; preserve input order so the Failed list reads in the
        // same order the operator selected.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ids = body.Ids
            .Where(id => !string.IsNullOrWhiteSpace(id) && seen.Add(id))
            .ToList();

        var failed = new List<BulkDeleteFailure>();
        var failedLock = new object();
        var succeeded = 0;

        using var gate = new SemaphoreSlim(BulkDeleteConcurrency, BulkDeleteConcurrency);

        var tasks = ids.Select(async id =>
        {
            await gate.WaitAsync(ct);
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var box = scope.ServiceProvider.GetRequiredService<BoxClient>();
                await deleteOne(box, id, ct);
                Interlocked.Increment(ref succeeded);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Caller hung up — let the cancellation propagate out of WhenAll.
                throw;
            }
            catch (Exception ex)
            {
                lock (failedLock)
                {
                    failed.Add(new BulkDeleteFailure(id, ex.Message));
                }
                _logger.LogWarning(ex,
                    "Bulk delete item failed (id={Id}): {Message}", id, ex.Message);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);

        _logger.LogInformation(
            "Bulk delete finished: requested={Requested} succeeded={Succeeded} failed={Failed}",
            ids.Count, succeeded, failed.Count);

        return Ok(new BulkDeleteResponse(ids.Count, succeeded, failed));
    }
}

using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Source.Features.BoxManagement;
using Source.Features.CiPublish;
using Source.Features.RuntimeTemplates.Commands.UpdateRuntimeTemplateStatus;
using Source.Features.RuntimeTemplates.Models;
using Source.Infrastructure;
using Source.Infrastructure.AuthorizationModels;

namespace Source.Features.RuntimeTemplates.Controllers;

/// <summary>
/// Operator surface for the golden-template catalog. Backs the registry of every
/// template box runtimes are forked from, plus a live discovery endpoint that lets a
/// super-admin browse the account's boxes before deciding which one to register —
/// the Box-native successor to the old Fly registry-tags picker.
///
/// <para><b>Why no MediatR for the basic CRUD.</b> List / GetById / latest-active are
/// one-liners over <see cref="ApplicationDbContext.RuntimeTemplates"/>; wrapping in
/// commands would add four files per endpoint without changing behaviour. The
/// status-update endpoint is the exception — it has a real invariant (single Active
/// row) and rides through MediatR (<see cref="UpdateRuntimeTemplateStatusCommand"/>)
/// so the transition is testable in isolation.</para>
///
/// <para><b>Authorisation model.</b> Every mutating endpoint requires
/// <see cref="RoleConstants.SuperAdmin"/>, except registration and status updates
/// which also accept the CI publish policy so <c>scripts/build-box-template.sh</c>
/// can self-register the template it just built. <c>GET latest-active</c> stays open
/// to any authenticated user because backend services look up the default fork
/// source via this endpoint.</para>
/// </summary>
[ApiController]
[Route("api/admin/runtime-templates")]
[Authorize] // baseline: any authenticated user (the latest-active lookup needs this);
            // every other action additionally gates on RoleConstants.SuperAdmin below.
[Tags("RuntimeTemplates")]
public class RuntimeTemplatesController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IMediator _mediator;
    private readonly BoxClient _box;
    private readonly ILogger<RuntimeTemplatesController> _logger;

    public RuntimeTemplatesController(
        ApplicationDbContext db,
        IMediator mediator,
        BoxClient box,
        ILogger<RuntimeTemplatesController> logger)
    {
        _db = db;
        _mediator = mediator;
        _box = box;
        _logger = logger;
    }

    /// <summary>
    /// Register a template box into the catalog. BoxId is the natural idempotency key —
    /// duplicates return 409. The new row is created as
    /// <see cref="RuntimeTemplateStatus.Active"/> and every other Active row is demoted
    /// to <see cref="RuntimeTemplateStatus.Deprecated"/> in the same transaction (the
    /// build script uses this path so a freshly-built template becomes the default fork
    /// source immediately).
    /// </summary>
    [HttpPost]
    [Authorize(Policy = CiPublishAuthenticationDefaults.PublishPolicy)]
    [ProducesResponseType(typeof(RuntimeTemplate), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RuntimeTemplate>> Register(
        [FromBody] RegisterRuntimeTemplateRequest req,
        CancellationToken ct)
    {
        var exists = await _db.RuntimeTemplates.AnyAsync(t => t.BoxId == req.BoxId, ct);
        if (exists)
        {
            return Conflict(new { error = $"Box '{req.BoxId}' is already registered as a template" });
        }

        // Preserve the single-Active invariant — same demotion logic as
        // UpdateRuntimeTemplateStatusHandler when promoting to Active.
        var previouslyActive = await _db.RuntimeTemplates
            .Where(t => t.Status == RuntimeTemplateStatus.Active)
            .ToListAsync(ct);
        foreach (var active in previouslyActive)
        {
            active.Status = RuntimeTemplateStatus.Deprecated;
        }

        var template = new RuntimeTemplate
        {
            Id = Guid.NewGuid(),
            BoxId = req.BoxId,
            Label = req.Label,
            GitSha = req.GitSha,
            BuiltAt = req.BuiltAt,
            Notes = req.Notes,
            Status = RuntimeTemplateStatus.Active,
        };
        _db.RuntimeTemplates.Add(template);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "RuntimeTemplate registered: box={BoxId}, label={Label}, sha={Sha}",
            req.BoxId, req.Label, req.GitSha);

        return CreatedAtAction(nameof(GetById), new { id = template.Id }, template);
    }

    /// <summary>
    /// Paged list of registered templates, newest <see cref="RuntimeTemplate.BuiltAt"/>
    /// first. Optional <paramref name="status"/> filter is case-insensitive; unknown
    /// values are silently ignored (no 400).
    /// </summary>
    [HttpGet]
    [Authorize(Roles = RoleConstants.SuperAdmin)]
    [ProducesResponseType(typeof(RuntimeTemplatesResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<RuntimeTemplatesResponse>> List(
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        // Hard cap — protects the host from "?pageSize=10_000" DoS by accident.
        pageSize = Math.Min(pageSize, 200);
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 1;

        var q = _db.RuntimeTemplates.AsQueryable();
        if (!string.IsNullOrEmpty(status)
            && Enum.TryParse<RuntimeTemplateStatus>(status, ignoreCase: true, out var parsed))
        {
            q = q.Where(t => t.Status == parsed);
        }

        var total = await q.CountAsync(ct);
        var items = await q
            .OrderByDescending(t => t.BuiltAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(new RuntimeTemplatesResponse(items, total, page, pageSize));
    }

    /// <summary>Fetch a single registered template by id.</summary>
    [HttpGet("{id:guid}")]
    [Authorize(Roles = RoleConstants.SuperAdmin)]
    [ProducesResponseType(typeof(RuntimeTemplate), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RuntimeTemplate>> GetById(Guid id, CancellationToken ct)
    {
        var template = await _db.RuntimeTemplates.FindAsync(new object[] { id }, ct);
        return template is null ? NotFound() : Ok(template);
    }

    /// <summary>
    /// Newest <see cref="RuntimeTemplateStatus.Active"/> template — the default fork
    /// source. Open to any authenticated caller because backend services need this
    /// lookup; it leaks no real secrets (the box id is the operational surface anyway).
    /// </summary>
    [HttpGet("latest-active")]
    // No SuperAdmin gate — the controller-level [Authorize] still requires an
    // authenticated user. Backend services rely on this lookup and don't carry
    // the SuperAdmin role.
    [ProducesResponseType(typeof(RuntimeTemplate), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RuntimeTemplate>> LatestActive(CancellationToken ct)
    {
        var template = await _db.RuntimeTemplates
            .Where(t => t.Status == RuntimeTemplateStatus.Active)
            .OrderByDescending(t => t.BuiltAt)
            .FirstOrDefaultAsync(ct);
        return template is null ? NotFound() : Ok(template);
    }

    /// <summary>
    /// Live list of the account's boxes as template candidates — drives the
    /// super-admin "pick a box to register" picker; nothing here mutates the local DB.
    /// Each row is flagged when the box is already registered so the UI can grey it out.
    /// Surfaces Box API failures as 502 with the reason in the body.
    /// </summary>
    [HttpGet("boxes")]
    [Authorize(Roles = RoleConstants.SuperAdmin)]
    [ProducesResponseType(typeof(List<TemplateCandidateBoxDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<List<TemplateCandidateBoxDto>>> CandidateBoxes(CancellationToken ct)
    {
        List<Source.Features.BoxManagement.Models.BoxVm> boxes;
        try
        {
            boxes = await _box.ListBoxesAsync(ct);
        }
        catch (BoxApiException ex)
        {
            _logger.LogWarning(ex, "RuntimeTemplates: Box ListBoxes failed ({Code})", ex.ErrorCode);
            return StatusCode(StatusCodes.Status502BadGateway,
                new { message = $"Box API call failed: {ex.ErrorCode ?? ex.StatusCode.ToString()}" });
        }

        var registered = await _db.RuntimeTemplates
            .Select(t => t.BoxId)
            .ToListAsync(ct);
        var registeredSet = new HashSet<string>(registered, StringComparer.Ordinal);

        var rows = boxes
            .Select(b => new TemplateCandidateBoxDto(
                Id: b.Id,
                Name: b.Name,
                Status: b.Status,
                Size: b.Size,
                Region: b.Region,
                CreatedAt: b.CreatedAt,
                AlreadyRegistered: registeredSet.Contains(b.Id)))
            .OrderByDescending(r => r.CreatedAt)
            .ToList();

        return Ok(rows);
    }

    /// <summary>
    /// Update a registered template's lifecycle status. Routed through MediatR because
    /// promoting a row to <see cref="RuntimeTemplateStatus.Active"/> demotes every other
    /// Active row in the same transaction — the single-Active invariant the provisioner
    /// relies on.
    /// </summary>
    [HttpPatch("{id:guid}/status")]
    [Authorize(Policy = CiPublishAuthenticationDefaults.PublishPolicy)]
    [ProducesResponseType(typeof(RuntimeTemplate), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RuntimeTemplate>> UpdateStatus(
        Guid id,
        [FromBody] UpdateRuntimeTemplateStatusRequest req,
        CancellationToken ct)
    {
        var result = await _mediator.Send(new UpdateRuntimeTemplateStatusCommand(id, req.Status), ct);
        if (result.IsFailure)
        {
            return result.Error == UpdateRuntimeTemplateStatusHandler.NotFoundError
                ? NotFound()
                : BadRequest(new { error = result.Error });
        }
        return Ok(result.Value);
    }
}

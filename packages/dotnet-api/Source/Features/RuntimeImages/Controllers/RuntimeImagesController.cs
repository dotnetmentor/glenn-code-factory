using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Source.Features.CiPublish;
using Source.Features.RuntimeImages.Commands.UpdateRuntimeImageStatus;
using Source.Features.RuntimeImages.Models;
using Source.Features.RuntimeImages.Services;
using Source.Infrastructure;
using Source.Infrastructure.AuthorizationModels;

namespace Source.Features.RuntimeImages.Controllers;

/// <summary>
/// Operator surface for the runtime base image catalog. Backs the registry of every
/// published image the runtime spawns Machines from, plus the Fly registry discovery
/// endpoint that lets a super-admin browse pushed tags before deciding what to register.
///
/// <para><b>Why no MediatR for the basic CRUD.</b> List / GetById / latest-active are
/// one-liners over <see cref="ApplicationDbContext.RuntimeImages"/>; wrapping in commands
/// would add four files per endpoint without changing behaviour. Mirrors the
/// <see cref="Source.Features.FlyManagement.Controllers.FlyAdminController"/> shape. The
/// status-update endpoint is the exception — it has a real invariant (single Active row)
/// and rides through MediatR (<see cref="UpdateRuntimeImageStatusCommand"/>) so the
/// transition is testable in isolation.</para>
///
/// <para><b>Authorisation model.</b> Every mutating endpoint requires
/// <see cref="RoleConstants.SuperAdmin"/>. Registration used to also accept a CI-only
/// publisher token in <c>X-Publisher-Token</c> — that flow is retired: build pipelines
/// now publish to the registry and a human picks/registers from the super-admin UI.
/// <c>GET latest-active</c> stays open to any authenticated user because backend services
/// look up the default spawn target via this endpoint.</para>
/// </summary>
[ApiController]
[Route("api/admin/runtime-images")]
[Authorize] // baseline: any authenticated user (the latest-active lookup needs this);
            // every other action additionally gates on RoleConstants.SuperAdmin below.
[Tags("RuntimeImages")]
public class RuntimeImagesController : ControllerBase
{
    /// <summary>
    /// AppSettings key for the default image name we ask the registry about when no
    /// <c>imageName</c> query parameter is supplied. Centralised here so the controller
    /// is the only place that knows the key — moving it to a typed options class would
    /// be premature for one string.
    /// </summary>
    public const string DefaultImageNameConfigKey = "RuntimeImages:DefaultImageName";

    /// <summary>
    /// Fallback when neither <c>RuntimeImages:DefaultImageName</c> in appsettings nor an
    /// <c>imageName</c> query string is supplied. Matches the production image name the
    /// publish-runtime-image.sh script ships.
    /// </summary>
    public const string FallbackImageName = "glenn-runtime-base";

    private readonly ApplicationDbContext _db;
    private readonly IMediator _mediator;
    private readonly IFlyRegistryClient _registry;
    private readonly IConfiguration _config;
    private readonly ILogger<RuntimeImagesController> _logger;

    public RuntimeImagesController(
        ApplicationDbContext db,
        IMediator mediator,
        IFlyRegistryClient registry,
        IConfiguration config,
        ILogger<RuntimeImagesController> logger)
    {
        _db = db;
        _mediator = mediator;
        _registry = registry;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Register a runtime image into the catalog. SuperAdmin-only. Tag is the natural
    /// idempotency key — duplicates return 409. The image is created in
    /// <see cref="RuntimeImageStatus.Active"/>; promotion semantics (single-Active
    /// invariant) only kick in via the status-update endpoint, so the operator should
    /// activate explicitly after register if they want exactly one Active row.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = CiPublishAuthenticationDefaults.PublishPolicy)]
    [ProducesResponseType(typeof(RuntimeImage), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RuntimeImage>> Register(
        [FromBody] RegisterRuntimeImageRequest req,
        CancellationToken ct)
    {
        var exists = await _db.RuntimeImages.AnyAsync(i => i.Tag == req.Tag, ct);
        if (exists)
        {
            return Conflict(new { error = $"Tag '{req.Tag}' already exists" });
        }

        // Preserve the single-Active invariant — same demotion logic as
        // UpdateRuntimeImageStatusHandler when promoting to Active.
        var previouslyActive = await _db.RuntimeImages
            .Where(i => i.Status == RuntimeImageStatus.Active)
            .ToListAsync(ct);
        foreach (var activeImage in previouslyActive)
        {
            activeImage.Status = RuntimeImageStatus.Deprecated;
        }

        var newImage = new RuntimeImage
        {
            Id = Guid.NewGuid(),
            Tag = req.Tag,
            Digest = req.Digest,
            Registry = req.Registry,
            GitSha = req.GitSha,
            BuiltAt = req.BuiltAt,
            SizeMb = req.SizeMb,
            Notes = req.Notes,
            Status = RuntimeImageStatus.Active,
        };
        _db.RuntimeImages.Add(newImage);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "RuntimeImage registered: tag={Tag}, sha={Sha}",
            req.Tag,
            req.GitSha);

        return CreatedAtAction(nameof(GetById), new { id = newImage.Id }, newImage);
    }

    /// <summary>
    /// Paged list of registered images, newest <see cref="RuntimeImage.BuiltAt"/> first.
    /// Optional <paramref name="status"/> filter is case-insensitive; unknown values are
    /// silently ignored (no 400) to match the Fly admin operations endpoint shape.
    /// </summary>
    [HttpGet]
    [Authorize(Roles = RoleConstants.SuperAdmin)]
    [ProducesResponseType(typeof(RuntimeImagesResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<RuntimeImagesResponse>> List(
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        // Hard cap — protects the host from "?pageSize=10_000" DoS by accident.
        pageSize = Math.Min(pageSize, 200);
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 1;

        var q = _db.RuntimeImages.AsQueryable();
        if (!string.IsNullOrEmpty(status)
            && Enum.TryParse<RuntimeImageStatus>(status, ignoreCase: true, out var parsed))
        {
            q = q.Where(i => i.Status == parsed);
        }

        var total = await q.CountAsync(ct);
        var items = await q
            .OrderByDescending(i => i.BuiltAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(new RuntimeImagesResponse(items, total, page, pageSize));
    }

    /// <summary>Fetch a single registered image by id.</summary>
    [HttpGet("{id:guid}")]
    [Authorize(Roles = RoleConstants.SuperAdmin)]
    [ProducesResponseType(typeof(RuntimeImage), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RuntimeImage>> GetById(Guid id, CancellationToken ct)
    {
        var img = await _db.RuntimeImages.FindAsync(new object[] { id }, ct);
        return img is null ? NotFound() : Ok(img);
    }

    /// <summary>
    /// Newest <see cref="RuntimeImageStatus.Active"/> image — the default spawn target.
    /// Open to any authenticated caller because other backend services need this lookup;
    /// it leaks no real secrets (registry path + tag are the operational surface anyway).
    /// </summary>
    [HttpGet("latest-active")]
    // No SuperAdmin gate — the controller-level [Authorize] still requires an
    // authenticated user. Backend services rely on this lookup to find the default
    // spawn target and they don't carry the SuperAdmin role.
    [ProducesResponseType(typeof(RuntimeImage), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RuntimeImage>> LatestActive(CancellationToken ct)
    {
        var img = await _db.RuntimeImages
            .Where(i => i.Status == RuntimeImageStatus.Active)
            .OrderByDescending(i => i.BuiltAt)
            .FirstOrDefaultAsync(ct);
        return img is null ? NotFound() : Ok(img);
    }

    /// <summary>
    /// List every tag currently pushed to the Fly registry under <paramref name="imageName"/>
    /// (or the configured default if omitted). Each item carries the manifest digest, the
    /// build-time size, the image's <c>created</c> timestamp, and — when the build stamped
    /// it — the source git SHA. Drives the super-admin UI's "pick something to register"
    /// picker; nothing here mutates the local DB.
    ///
    /// <para>Failure modes are surfaced with operator-friendly status codes:
    /// <list type="bullet">
    ///   <item>404 → the image name does not exist on the registry;</item>
    ///   <item>502 → the registry is unreachable, returned a 5xx, or the auth token is
    ///         missing / wrong scope (the body explains which);</item>
    ///   <item>200 → tags array, possibly empty.</item>
    /// </list>
    /// We deliberately do not return a partial list when individual manifest fetches fail —
    /// callers expect a row's <c>digest</c> field to be populated, so a single bad manifest
    /// stops the batch. In practice this only happens during a transient registry outage
    /// and the user just retries.</para>
    /// </summary>
    [HttpGet("registry-tags")]
    [Authorize(Roles = RoleConstants.SuperAdmin)]
    [ProducesResponseType(typeof(List<RegistryTagDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<List<RegistryTagDto>>> RegistryTags(
        [FromQuery] string? imageName,
        CancellationToken ct)
    {
        var name = !string.IsNullOrWhiteSpace(imageName)
            ? imageName
            : (_config[DefaultImageNameConfigKey] ?? FallbackImageName);

        try
        {
            var tags = await _registry.ListTagsAsync(name, ct);

            var rows = new List<RegistryTagDto>(tags.Count);
            foreach (var tag in tags)
            {
                var info = await _registry.GetManifestAsync(name, tag, ct);
                info.Labels.TryGetValue(FlyRegistryClient.GitShaLabelKey, out var gitSha);

                rows.Add(new RegistryTagDto(
                    Tag: tag,
                    Digest: info.Digest,
                    SizeBytes: info.SizeBytes > 0 ? info.SizeBytes : null,
                    PushedAt: info.PushedAt,
                    GitSha: string.IsNullOrEmpty(gitSha) ? null : gitSha));
            }

            // Newest first when we have timestamps; tags without one drop to the bottom.
            rows.Sort((a, b) =>
                Nullable.Compare(b.PushedAt, a.PushedAt));

            return Ok(rows);
        }
        catch (FlyRegistryException ex)
        {
            _logger.LogWarning(ex,
                "Fly registry call failed for image {Image}: {Kind}",
                name, ex.Kind);

            return ex.Kind switch
            {
                FlyRegistryErrorKind.NotFound =>
                    NotFound(new { message = $"Image '{name}' not found on Fly registry." }),
                FlyRegistryErrorKind.Unauthorized =>
                    StatusCode(StatusCodes.Status502BadGateway,
                        new { message = $"Fly registry rejected our credentials: {ex.Message}" }),
                FlyRegistryErrorKind.Transport =>
                    StatusCode(StatusCodes.Status502BadGateway,
                        new { message = $"Fly registry unreachable: {ex.Message}" }),
                FlyRegistryErrorKind.Protocol =>
                    StatusCode(StatusCodes.Status502BadGateway,
                        new { message = $"Fly registry returned malformed data: {ex.Message}" }),
                _ =>
                    StatusCode(StatusCodes.Status502BadGateway,
                        new { message = $"Fly registry call failed: {ex.Message}" }),
            };
        }
    }

    /// <summary>
    /// Update a registered image's lifecycle status. Routed through MediatR
    /// (<see cref="UpdateRuntimeImageStatusCommand"/>) because promoting a row to
    /// <see cref="RuntimeImageStatus.Active"/> demotes every other Active row to
    /// <see cref="RuntimeImageStatus.Deprecated"/> in the same transaction — the
    /// single-Active invariant the provisioner relies on.
    /// </summary>
    [HttpPatch("{id:guid}/status")]
    [Authorize(Policy = CiPublishAuthenticationDefaults.PublishPolicy)]
    [ProducesResponseType(typeof(RuntimeImage), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RuntimeImage>> UpdateStatus(
        Guid id,
        [FromBody] UpdateRuntimeImageStatusRequest req,
        CancellationToken ct)
    {
        var result = await _mediator.Send(new UpdateRuntimeImageStatusCommand(id, req.Status), ct);
        if (result.IsFailure)
        {
            return result.Error == UpdateRuntimeImageStatusHandler.NotFoundError
                ? NotFound()
                : BadRequest(new { error = result.Error });
        }
        return Ok(result.Value);
    }

    /// <summary>
    /// Mark an image deprecated — still bootable, but no longer the preferred default.
    /// Kept for backwards compatibility with the original card-2 admin surface; new
    /// callers should prefer <c>PATCH {id}/status</c> with body <c>{"status":"Deprecated"}</c>.
    /// </summary>
    [HttpPost("{id:guid}/deprecate")]
    [Authorize(Roles = RoleConstants.SuperAdmin)]
    [ProducesResponseType(typeof(RuntimeImage), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RuntimeImage>> Deprecate(Guid id, CancellationToken ct)
    {
        var img = await _db.RuntimeImages.FindAsync(new object[] { id }, ct);
        if (img is null) return NotFound();
        img.Status = RuntimeImageStatus.Deprecated;
        await _db.SaveChangesAsync(ct);
        return Ok(img);
    }

    /// <summary>
    /// Yank an image — must not be used to spawn new machines. Row is kept (not deleted)
    /// so the audit trail of what was once published survives.
    /// </summary>
    [HttpPost("{id:guid}/yank")]
    [Authorize(Roles = RoleConstants.SuperAdmin)]
    [ProducesResponseType(typeof(RuntimeImage), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RuntimeImage>> Yank(Guid id, CancellationToken ct)
    {
        var img = await _db.RuntimeImages.FindAsync(new object[] { id }, ct);
        if (img is null) return NotFound();
        img.Status = RuntimeImageStatus.Yanked;
        await _db.SaveChangesAsync(ct);
        _logger.LogWarning("RuntimeImage yanked: id={Id}, tag={Tag}", img.Id, img.Tag);
        return Ok(img);
    }
}

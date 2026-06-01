using System.ComponentModel.DataAnnotations;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Source.Features.ProjectTemplates.Commands.ArchiveProjectTemplate;
using Source.Features.ProjectTemplates.Commands.CreateProjectTemplate;
using Source.Features.ProjectTemplates.Commands.UpdateProjectTemplate;
using Source.Features.ProjectTemplates.Queries.GetProjectTemplate;
using Source.Features.ProjectTemplates.Queries.ListProjectTemplates;
using Source.Infrastructure.AuthorizationModels;
using Source.Shared.Controllers;
using Source.Shared.Results;

namespace Source.Features.ProjectTemplates.Controllers;

/// <summary>
/// REST endpoints for the global <see cref="Models.ProjectTemplate"/> catalogue
/// — the "Starters" feature in the UI. Two route prefixes share one controller
/// (and one Swagger tag, so Orval generates one cohesive hook surface):
///
/// <list type="bullet">
///   <item><c>api/project-templates</c> — user-facing picker for the new-project
///         screen. Authenticated only; returns active, non-archived rows.</item>
///   <item><c>api/admin/project-templates</c> — admin CRUD. Gated to
///         <see cref="RoleConstants.SuperAdmin"/> via the per-method attribute.</item>
/// </list>
///
/// <para><b>Return types.</b> Every action declares an explicit
/// <c>ActionResult&lt;T&gt;</c> with a matching
/// <see cref="ProducesResponseTypeAttribute"/> so Swagger / Orval emit concrete
/// TypeScript types — see <c>backoffice-web/api/queries-commands</c>.</para>
///
/// <para><b>Error mapping.</b> Handlers emit stable error codes; the controller
/// owns translation to HTTP status:
/// <list type="bullet">
///   <item><c>not_authorized</c> → 403</item>
///   <item><c>*_not_found</c> → 404</item>
///   <item><c>*_taken</c> → 409</item>
///   <item><c>spec_invalid</c> / <c>invalid_*</c> → 400</item>
/// </list></para>
/// </summary>
[ApiController]
[Authorize]
[Tags("ProjectTemplates")]
public sealed class ProjectTemplatesController : BaseApiController
{
    public ProjectTemplatesController(IMediator mediator, ILogger<ProjectTemplatesController> logger)
        : base(mediator, logger) { }

    // -------- Picker route (authenticated, active only) --------

    /// <summary>
    /// List active, non-archived starters for the new-project picker. Ordered
    /// by <c>SortOrder</c> ASC then <c>Name</c> ASC. Any authenticated caller
    /// can read this — the picker is rendered on the user-facing new-project
    /// screen.
    /// </summary>
    [HttpGet("/api/project-templates")]
    [ProducesResponseType<List<ProjectTemplateListItem>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<List<ProjectTemplateListItem>>> ListForPicker(
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var result = await Mediator.Send(
            new ListProjectTemplatesQuery(CallerUserId: userId, IncludeArchived: false),
            cancellationToken);

        if (result.IsFailure)
        {
            return MapFailure<List<ProjectTemplateListItem>>(result.Error);
        }

        // Picker contract is "active starters only" — the handler already
        // hides archived rows by honouring the global soft-delete filter
        // (IncludeArchived=false); here we additionally drop IsActive=false
        // rows so admins can stage a starter (IsActive=false) without leaking
        // it to the picker.
        var picker = result.Value.Where(t => t.IsActive).ToList();
        return Ok(picker);
    }

    // -------- Admin routes (super-admin only) --------

    /// <summary>
    /// List every starter — including <c>IsActive=false</c> and soft-deleted
    /// rows — for the super-admin "Manage Starters" page.
    /// </summary>
    [HttpGet("/api/admin/project-templates")]
    [Authorize(Roles = RoleConstants.SuperAdmin)]
    [ProducesResponseType<List<ProjectTemplateListItem>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<List<ProjectTemplateListItem>>> ListForAdmin(
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var result = await Mediator.Send(
            new ListProjectTemplatesQuery(CallerUserId: userId, IncludeArchived: true),
            cancellationToken);

        return MapResult(result);
    }

    /// <summary>
    /// Fetch one starter (including soft-deleted) with its full inline
    /// <c>RuntimeSpec</c> JSON. Used by the admin edit screen.
    /// </summary>
    [HttpGet("/api/admin/project-templates/{templateId:guid}")]
    [Authorize(Roles = RoleConstants.SuperAdmin)]
    [ProducesResponseType<ProjectTemplateDetail>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProjectTemplateDetail>> GetOne(
        Guid templateId,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var result = await Mediator.Send(
            new GetProjectTemplateQuery(templateId, userId),
            cancellationToken);

        return MapResult(result);
    }

    /// <summary>
    /// Create a new starter. Slug + name must be unique among non-tombstoned
    /// rows. Non-null <c>RuntimeSpec</c> is validated against
    /// <c>RuntimeSpecV3</c> — invalid JSON returns <c>spec_invalid</c> / 400.
    /// </summary>
    [HttpPost("/api/admin/project-templates")]
    [Authorize(Roles = RoleConstants.SuperAdmin)]
    [ProducesResponseType<ProjectTemplateDetail>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProjectTemplateDetail>> Create(
        [FromBody] CreateProjectTemplateRequest? request,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        if (request is null)
        {
            return BadRequest(new { error = "invalid_body" });
        }

        var result = await Mediator.Send(new CreateProjectTemplateCommand(
            CallerUserId: userId,
            Name: request.Name,
            Slug: request.Slug,
            Description: request.Description,
            IconKey: request.IconKey,
            SourceRepoOwner: request.SourceRepoOwner,
            SourceRepoName: request.SourceRepoName,
            RuntimeSpec: request.RuntimeSpec,
            IsActive: request.IsActive,
            IsDefault: request.IsDefault,
            SortOrder: request.SortOrder), cancellationToken);

        if (result.IsSuccess)
        {
            return CreatedAtAction(
                nameof(GetOne),
                new { templateId = result.Value.Id },
                result.Value);
        }
        return MapFailure<ProjectTemplateDetail>(result.Error);
    }

    /// <summary>
    /// Replace a starter's mutable fields. Body shape mirrors create — the
    /// endpoint is a full replace, not a JSON patch.
    /// </summary>
    [HttpPut("/api/admin/project-templates/{templateId:guid}")]
    [Authorize(Roles = RoleConstants.SuperAdmin)]
    [ProducesResponseType<ProjectTemplateDetail>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProjectTemplateDetail>> Update(
        Guid templateId,
        [FromBody] UpdateProjectTemplateRequest? request,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        if (request is null)
        {
            return BadRequest(new { error = "invalid_body" });
        }

        var result = await Mediator.Send(new UpdateProjectTemplateCommand(
            TemplateId: templateId,
            CallerUserId: userId,
            Name: request.Name,
            Slug: request.Slug,
            Description: request.Description,
            IconKey: request.IconKey,
            SourceRepoOwner: request.SourceRepoOwner,
            SourceRepoName: request.SourceRepoName,
            RuntimeSpec: request.RuntimeSpec,
            IsActive: request.IsActive,
            IsDefault: request.IsDefault,
            SortOrder: request.SortOrder), cancellationToken);

        return MapResult(result);
    }

    /// <summary>
    /// Archive (soft-delete) a starter. Idempotent — already-archived rows
    /// still return 204. Existing projects that reference this starter via
    /// <c>Project.TemplateId</c> are unaffected.
    /// </summary>
    [HttpDelete("/api/admin/project-templates/{templateId:guid}")]
    [Authorize(Roles = RoleConstants.SuperAdmin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> Archive(
        Guid templateId,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var result = await Mediator.Send(
            new ArchiveProjectTemplateCommand(templateId, userId),
            cancellationToken);

        if (result.IsSuccess) return NoContent();
        return MapFailureNonGeneric(result.Error);
    }

    // -------- Error code mapping --------
    //
    // not_authorized                  -> 403
    // template_not_found              -> 404
    // name_taken / slug_taken         -> 409
    // spec_invalid                    -> 400
    // invalid_*                       -> 400 (default)

    private ActionResult<T> MapResult<T>(Result<T> result)
    {
        if (result.IsSuccess) return Ok(result.Value);
        return MapFailure<T>(result.Error);
    }

    private ActionResult<T> MapFailure<T>(string? error)
    {
        var body = new { error = error ?? "unknown_error" };
        return error switch
        {
            "not_authorized" => StatusCode(StatusCodes.Status403Forbidden, body),
            "template_not_found" => NotFound(body),
            "name_taken" => Conflict(body),
            "slug_taken" => Conflict(body),
            _ => BadRequest(body),
        };
    }

    private ActionResult MapFailureNonGeneric(string? error)
    {
        var body = new { error = error ?? "unknown_error" };
        return error switch
        {
            "not_authorized" => StatusCode(StatusCodes.Status403Forbidden, body),
            "template_not_found" => NotFound(body),
            "name_taken" => Conflict(body),
            "slug_taken" => Conflict(body),
            _ => BadRequest(body),
        };
    }
}

// -------- Request DTOs --------

/// <summary>
/// Wire shape for <c>POST /api/admin/project-templates</c>. Mirrors the
/// <see cref="Models.ProjectTemplate"/> field set 1:1 — there is no separate
/// "admin-only" subset because the picker reads via the GET routes, not POST.
/// </summary>
public sealed class CreateProjectTemplateRequest
{
    [Required, StringLength(100, MinimumLength = 1)]
    public required string Name { get; init; }

    [Required, StringLength(100, MinimumLength = 1)]
    public required string Slug { get; init; }

    [StringLength(500)]
    public string? Description { get; init; }

    [StringLength(50)]
    public string? IconKey { get; init; }

    [Required, StringLength(120, MinimumLength = 1)]
    public required string SourceRepoOwner { get; init; }

    [Required, StringLength(120, MinimumLength = 1)]
    public required string SourceRepoName { get; init; }

    /// <summary>
    /// Inline V3 runtime-spec JSON document, or <c>null</c> for an empty starter
    /// (runtime boots with the default/empty spec). Validated against
    /// <c>RuntimeSpecV3</c> in the handler.
    /// </summary>
    public string? RuntimeSpec { get; init; }

    public bool IsActive { get; init; } = true;
    public bool IsDefault { get; init; } = false;
    public int SortOrder { get; init; } = 0;
}

/// <summary>
/// Wire shape for <c>PUT /api/admin/project-templates/{id}</c>. Same shape as
/// <see cref="CreateProjectTemplateRequest"/> — update is a full replace.
/// </summary>
public sealed class UpdateProjectTemplateRequest
{
    [Required, StringLength(100, MinimumLength = 1)]
    public required string Name { get; init; }

    [Required, StringLength(100, MinimumLength = 1)]
    public required string Slug { get; init; }

    [StringLength(500)]
    public string? Description { get; init; }

    [StringLength(50)]
    public string? IconKey { get; init; }

    [Required, StringLength(120, MinimumLength = 1)]
    public required string SourceRepoOwner { get; init; }

    [Required, StringLength(120, MinimumLength = 1)]
    public required string SourceRepoName { get; init; }

    public string? RuntimeSpec { get; init; }

    public bool IsActive { get; init; } = true;
    public bool IsDefault { get; init; } = false;
    public int SortOrder { get; init; } = 0;
}

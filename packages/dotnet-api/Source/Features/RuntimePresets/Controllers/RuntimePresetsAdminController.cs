using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Source.Features.RuntimePresets.Commands.ClonePreset;
using Source.Features.RuntimePresets.Commands.CreatePreset;
using Source.Features.RuntimePresets.Commands.DeletePreset;
using Source.Features.RuntimePresets.Commands.UpdatePreset;
using Source.Features.RuntimePresets.Dtos;
using Source.Features.RuntimePresets.Queries.GetAgentToolDescription;
using Source.Features.RuntimePresets.Queries.GetMiseVersions;
using Source.Features.RuntimePresets.Queries.GetPreset;
using Source.Features.RuntimePresets.Queries.ListPresets;
using Source.Features.RuntimePresets.Queries.PreviewPreset;
using Source.Infrastructure.AuthorizationModels;
using Source.Shared.Controllers;
using Source.Shared.Results;

namespace Source.Features.RuntimePresets.Controllers;

/// <summary>
/// REST endpoints for the super-admin <see cref="Models.ServicePreset"/>
/// catalogue — the "Runtime Presets" tab in the admin UI. Two route prefixes
/// share one controller (and one Swagger tag, so Orval generates one cohesive
/// hook surface):
///
/// <list type="bullet">
///   <item><c>api/admin/runtime-presets</c> — super-admin CRUD + preview +
///         mise-version lookup. Gated by
///         <see cref="RoleConstants.SuperAdmin"/>.</item>
///   <item><c>api/runtime-presets/tool-description</c> — anonymous read of the
///         agent's tool description + JSON schema. Fetched by the daemon at
///         startup before any user auth context exists.</item>
/// </list>
///
/// <para><b>Return types.</b> Every action declares an explicit
/// <c>ActionResult&lt;T&gt;</c> with a matching
/// <see cref="ProducesResponseTypeAttribute"/> so Swagger / Orval emit
/// concrete TypeScript types.</para>
///
/// <para><b>Error mapping.</b> Handlers emit stable error codes; the
/// controller owns translation to HTTP status:
/// <list type="bullet">
///   <item><c>preset_not_found</c> → 404</item>
///   <item><c>preset_built_in</c> → 409 (clone-only protection)</item>
///   <item><c>slug_taken</c> → 409</item>
///   <item><c>*_invalid</c> → 400</item>
/// </list></para>
/// </summary>
[ApiController]
[Authorize(Roles = RoleConstants.SuperAdmin)]
[Tags("RuntimePresetsAdmin")]
public sealed class RuntimePresetsAdminController : BaseApiController
{
    public RuntimePresetsAdminController(IMediator mediator, ILogger<RuntimePresetsAdminController> logger)
        : base(mediator, logger) { }

    // -------- Admin CRUD --------

    /// <summary>
    /// List every preset (built-in + user clones), ordered by category then
    /// display name. Soft-deleted rows are hidden by the global query filter.
    /// </summary>
    [HttpGet("/api/admin/runtime-presets")]
    [ProducesResponseType<List<ServicePresetDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<List<ServicePresetDto>>> List(CancellationToken cancellationToken)
    {
        var result = await Mediator.Send(new ListPresetsQuery(), cancellationToken);
        return MapResult(result);
    }

    /// <summary>
    /// Fetch one preset by id with deserialised env / parameters payload.
    /// </summary>
    [HttpGet("/api/admin/runtime-presets/{id:guid}", Name = "GetRuntimePresetById")]
    [ProducesResponseType<ServicePresetDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ServicePresetDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        var result = await Mediator.Send(new GetPresetQuery(id), cancellationToken);
        return MapResult(result);
    }

    /// <summary>
    /// Create a new user preset. <c>IsBuiltIn</c> is always false for
    /// admin-created rows (flipping built-in is a migration-only operation).
    /// </summary>
    [HttpPost("/api/admin/runtime-presets")]
    [ProducesResponseType<ServicePresetDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ServicePresetDto>> Create(
        [FromBody] CreatePresetRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new { error = "invalid_body" });
        }

        var result = await Mediator.Send(new CreatePresetCommand(
            Slug: request.Slug,
            DisplayName: request.DisplayName,
            Description: request.Description,
            Category: request.Category,
            IconName: request.IconName,
            CommandTemplate: request.CommandTemplate,
            EnvTemplate: request.EnvTemplate,
            HealthcheckCommand: request.HealthcheckCommand,
            HealthcheckInterval: request.HealthcheckInterval,
            DefaultUser: request.DefaultUser,
            Autorestart: request.Autorestart,
            InstallContribution: request.InstallContribution,
            SetupContribution: request.SetupContribution,
            InstallVerify: request.InstallVerify,
            Parameters: request.Parameters), cancellationToken);

        if (result.IsSuccess)
        {
            return CreatedAtRoute(
                "GetRuntimePresetById",
                new { id = result.Value.Id },
                result.Value);
        }
        return MapFailure<ServicePresetDto>(result.Error);
    }

    /// <summary>
    /// Replace a preset's mutable fields. Slug + <c>IsBuiltIn</c> are omitted
    /// from the request shape — both are immutable post-create.
    /// </summary>
    [HttpPut("/api/admin/runtime-presets/{id:guid}")]
    [ProducesResponseType<ServicePresetDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ServicePresetDto>> Update(
        Guid id,
        [FromBody] UpdatePresetRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new { error = "invalid_body" });
        }

        var result = await Mediator.Send(new UpdatePresetCommand(
            Id: id,
            DisplayName: request.DisplayName,
            Description: request.Description,
            Category: request.Category,
            IconName: request.IconName,
            CommandTemplate: request.CommandTemplate,
            EnvTemplate: request.EnvTemplate,
            HealthcheckCommand: request.HealthcheckCommand,
            HealthcheckInterval: request.HealthcheckInterval,
            DefaultUser: request.DefaultUser,
            Autorestart: request.Autorestart,
            InstallContribution: request.InstallContribution,
            SetupContribution: request.SetupContribution,
            InstallVerify: request.InstallVerify,
            Parameters: request.Parameters), cancellationToken);

        return MapResult(result);
    }

    /// <summary>
    /// Clone an existing preset under a new slug. Operators clone built-ins
    /// (they're read-only) before customising — the resulting row has
    /// <c>IsBuiltIn=false</c>.
    /// </summary>
    [HttpPost("/api/admin/runtime-presets/{id:guid}/clone")]
    [ProducesResponseType<ServicePresetDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ServicePresetDto>> Clone(
        Guid id,
        [FromBody] ClonePresetRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new { error = "invalid_body" });
        }

        var result = await Mediator.Send(new ClonePresetCommand(
            SourceId: id,
            NewSlug: request.NewSlug,
            NewDisplayName: request.NewDisplayName), cancellationToken);

        if (result.IsSuccess)
        {
            return CreatedAtRoute(
                "GetRuntimePresetById",
                new { id = result.Value.Id },
                result.Value);
        }
        return MapFailure<ServicePresetDto>(result.Error);
    }

    /// <summary>
    /// Soft-delete a user preset. Built-ins are not deletable — the handler
    /// returns <c>preset_built_in</c> and the controller maps to 409.
    /// </summary>
    [HttpDelete("/api/admin/runtime-presets/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await Mediator.Send(new DeletePresetCommand(id), cancellationToken);
        if (result.IsSuccess) return NoContent();
        return MapFailureNonGeneric(result.Error);
    }

    // -------- Helper endpoints --------

    /// <summary>
    /// List installable versions for a mise-managed tool — backs the
    /// "Lookup versions" affordance on a <c>miseTool</c>-flagged parameter
    /// in the admin editor.
    /// </summary>
    [HttpGet("/api/admin/runtime-presets/mise-versions")]
    [ProducesResponseType<MiseVersionsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<MiseVersionsResponse>> GetMiseVersions(
        [FromQuery] string tool,
        CancellationToken cancellationToken)
    {
        var result = await Mediator.Send(new GetMiseVersionsQuery(tool), cancellationToken);
        return MapResult(result);
    }

    /// <summary>
    /// Render the preset's templates against a sample value bag and return the
    /// per-field results plus a flat error list. Partial rendering supported —
    /// missing values surface in <c>errors</c> rather than collapsing the
    /// response into a 400.
    /// </summary>
    [HttpPost("/api/admin/runtime-presets/{id:guid}/preview")]
    [ProducesResponseType<PreviewPresetResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PreviewPresetResponse>> Preview(
        Guid id,
        [FromBody] PreviewPresetRequest? request,
        CancellationToken cancellationToken)
    {
        var values = request?.Values;
        var result = await Mediator.Send(new PreviewPresetQuery(id, values), cancellationToken);
        return MapResult(result);
    }

    // -------- Daemon-facing tool description (anonymous) --------

    /// <summary>
    /// Dynamic description + JSON schema for the agent's
    /// <c>propose_runtime_spec</c> tool, fetched by the daemon at startup
    /// before any user auth context is established. Anonymous on purpose —
    /// the response leaks no user-scoped data; it's a derived view of public
    /// preset metadata.
    /// </summary>
    [HttpGet("/api/runtime-presets/tool-description")]
    [AllowAnonymous]
    [ProducesResponseType<AgentToolDescriptionResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AgentToolDescriptionResponse>> GetAgentToolDescription(
        CancellationToken cancellationToken)
    {
        var result = await Mediator.Send(new GetAgentToolDescriptionQuery(), cancellationToken);
        return MapResult(result);
    }

    // -------- Error code mapping --------
    //
    // preset_not_found   -> 404
    // preset_built_in    -> 409 (clone-only protection)
    // slug_taken         -> 409
    // *_invalid          -> 400 (default)

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
            "preset_not_found" => NotFound(body),
            "preset_built_in" => Conflict(body),
            "slug_taken" => Conflict(body),
            _ => BadRequest(body),
        };
    }

    private ActionResult MapFailureNonGeneric(string? error)
    {
        var body = new { error = error ?? "unknown_error" };
        return error switch
        {
            "preset_not_found" => NotFound(body),
            "preset_built_in" => Conflict(body),
            "slug_taken" => Conflict(body),
            _ => BadRequest(body),
        };
    }
}

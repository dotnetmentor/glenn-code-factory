using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Source.Features.SystemSettings.Commands;
using Source.Features.SystemSettings.Queries;
using Source.Infrastructure.AuthorizationModels;
using Source.Shared.Controllers;

namespace Source.Features.SystemSettings.Controllers;

/// <summary>
/// SuperAdmin-only HTTP surface for the SystemSettings store.
/// Reads expose schema (catalog) + current state, write goes through
/// <see cref="UpdateSystemSettingCommand"/>. Secrets never leave this controller in cleartext.
/// </summary>
[ApiController]
[Route("api/system-settings")]
[Authorize(Roles = RoleConstants.SuperAdmin)]
[Tags("SystemSettings")]
public class SystemSettingsController : BaseApiController
{
    public SystemSettingsController(IMediator mediator, ILogger<SystemSettingsController> logger)
        : base(mediator, logger)
    {
    }

    /// <summary>
    /// Every catalog-registered setting + its current DB state. Secret rows have <c>value=null</c>
    /// regardless of whether they're populated; the UI surfaces them by <c>hasValue</c>.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<SystemSettingDto>>(200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<IReadOnlyList<SystemSettingDto>>> GetAll()
    {
        var result = await Mediator.Send(new GetSystemSettingsQuery());
        return HandleResult(result);
    }

    /// <summary>
    /// Schema-only projection of the catalog — drives the Tabs and the description text shown
    /// above each input on the admin page.
    /// </summary>
    [HttpGet("categories")]
    [ProducesResponseType<IReadOnlyList<SystemSettingCategoryDto>>(200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<IReadOnlyList<SystemSettingCategoryDto>>> GetCategories()
    {
        var result = await Mediator.Send(new GetSystemSettingCategoriesQuery());
        return HandleResult(result);
    }

    /// <summary>
    /// Update a single setting. <c>IsSecret</c> is authoritative from the catalog — request
    /// bodies cannot influence it. See <see cref="UpdateSystemSettingCommand"/> for the
    /// null-vs-empty-string-for-secrets contract.
    /// </summary>
    [HttpPut("{key}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult> Update(string key, [FromBody] UpdateSystemSettingRequest request)
    {
        var userId = GetCurrentUserId();
        var result = await Mediator.Send(new UpdateSystemSettingCommand(key, request.Value, userId));

        if (!result.IsSuccess)
        {
            Logger.LogWarning("System setting update rejected: {Error}", result.Error);
            return BadRequest(new { error = result.Error });
        }

        return NoContent();
    }
}

/// <summary>
/// Request body for <c>PUT /api/system-settings/{key}</c>.
/// <para><c>Value = null</c> clears the row. <c>Value = ""</c> for a secret with an existing value
/// is treated as "keep what's there" (the UI sends "" for an untouched secret box).</para>
/// </summary>
public record UpdateSystemSettingRequest
{
    public string? Value { get; init; }
}

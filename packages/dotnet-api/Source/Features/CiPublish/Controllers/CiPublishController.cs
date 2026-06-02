using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Source.Features.CiPublish.Models;
using Source.Features.CiPublish.Queries.GetCiPublishStatus;
using Source.Features.FlyManagement.Configuration;

namespace Source.Features.CiPublish.Controllers;

[ApiController]
[Route("api/ci")]
[Authorize(AuthenticationSchemes = CiPublishAuthenticationDefaults.SchemeName, Policy = CiPublishAuthenticationDefaults.CiPublishOnlyPolicy)]
[Tags("CiPublish")]
public class CiPublishController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IFlyOptionsAccessor _flyOptions;
    private readonly ILogger<CiPublishController> _logger;

    public CiPublishController(
        IMediator mediator,
        IFlyOptionsAccessor flyOptions,
        ILogger<CiPublishController> logger)
    {
        _mediator = mediator;
        _flyOptions = flyOptions;
        _logger = logger;
    }

    [HttpGet("publish-status")]
    [ProducesResponseType(typeof(CiPublishStatusDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<CiPublishStatusDto>> PublishStatus(
        [FromQuery] string? gitSha,
        CancellationToken ct)
    {
        var result = await _mediator.Send(new GetCiPublishStatusQuery(gitSha), ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new { error = result.Error });
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Fly registry login material for CI push steps. Returns the org
    /// <c>Fly:ApiToken</c> from System Settings — treat
    /// <c>CONTROL_PLANE_PUBLISH_API_KEY</c> as tier-0 (registry + publish APIs).
    /// Rotate both keys on leak; restrict GitHub secrets to protected environments.
    /// </summary>
    [HttpGet("registry-credentials")]
    [ProducesResponseType(typeof(CiRegistryCredentialsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<CiRegistryCredentialsDto> RegistryCredentials()
    {
        var fly = _flyOptions.Current;
        if (string.IsNullOrWhiteSpace(fly.ApiToken))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "Fly:ApiToken is not configured in System Settings.",
            });
        }

        _logger.LogInformation("CiPublish registry credentials issued");

        return Ok(new CiRegistryCredentialsDto
        {
            RegistryHost = "registry.fly.io",
            Username = "x",
            Password = fly.ApiToken,
        });
    }
}

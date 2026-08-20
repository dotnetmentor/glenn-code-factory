using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Source.Features.CiPublish.Models;
using Source.Features.CiPublish.Queries.GetCiPublishStatus;

namespace Source.Features.CiPublish.Controllers;

[ApiController]
[Route("api/ci")]
[Authorize(AuthenticationSchemes = CiPublishAuthenticationDefaults.SchemeName, Policy = CiPublishAuthenticationDefaults.CiPublishOnlyPolicy)]
[Tags("CiPublish")]
public class CiPublishController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ILogger<CiPublishController> _logger;

    public CiPublishController(
        IMediator mediator,
        ILogger<CiPublishController> logger)
    {
        _mediator = mediator;
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

}

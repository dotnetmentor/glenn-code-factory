using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Source.Features.Cloudflare.Commands;
using Source.Features.Cloudflare.Models;
using Source.Features.Cloudflare.Queries;
using Source.Infrastructure.AuthorizationModels;
using Source.Shared.Controllers;

namespace Source.Features.Cloudflare.Controllers;

/// <summary>
/// SuperAdmin-only HTTP surface for the preview-subdomain pool. Two endpoints
/// for Phase 1: list every row (audit / inventory) and batch-create N new
/// rows (admin "fill the pool" gesture).
///
/// <para>Authorisation matches
/// <see cref="Source.Features.SystemSettings.Controllers.SystemSettingsController"/>
/// — managing the pool requires SuperAdmin because each batch-create call hits
/// Cloudflare's billable API.</para>
/// </summary>
[ApiController]
[Route("api/cloudflare/subdomains")]
[Authorize(Roles = RoleConstants.SuperAdmin)]
[Tags("Cloudflare")]
public class CloudflareSubdomainsController : BaseApiController
{
    public CloudflareSubdomainsController(IMediator mediator, ILogger<CloudflareSubdomainsController> logger)
        : base(mediator, logger)
    {
    }

    /// <summary>
    /// List every pool row newest-first, including assignment info when a row
    /// has been claimed. Never returns the tunnel token.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<SubdomainAssignmentDto>>(200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<IReadOnlyList<SubdomainAssignmentDto>>> List()
    {
        var result = await Mediator.Send(new GetSubdomainsQuery());
        return HandleResult(result);
    }

    /// <summary>
    /// Batch-provision <c>count</c> new pool rows. <c>count</c> must be
    /// between 1 and 50 inclusive — the upper bound caps blast radius on a
    /// fat-fingered request (each row hits Cloudflare 3 times).
    ///
    /// <para>Partial success is reported in the response shape (<c>successCount</c>,
    /// <c>failedCount</c>, <c>items</c>). The status code is 200 even on
    /// partial failure — the operator wants to see how many made it through.</para>
    /// </summary>
    [HttpPost("batch")]
    [ProducesResponseType<BatchCreateSubdomainsResponse>(200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<BatchCreateSubdomainsResponse>> Batch(
        [FromBody] BatchCreateSubdomainsRequest request,
        CancellationToken ct)
    {
        if (request.Count < 1 || request.Count > 50)
        {
            return BadRequest(new { error = "count must be between 1 and 50" });
        }

        var result = await Mediator.Send(new BatchCreateSubdomainsCommand(request.Count), ct);
        return HandleResult(result);
    }
}

/// <summary>Request body for <c>POST /api/cloudflare/subdomains/batch</c>.</summary>
public record BatchCreateSubdomainsRequest
{
    /// <summary>Number of pool rows to provision. 1–50 inclusive.</summary>
    public required int Count { get; init; }
}

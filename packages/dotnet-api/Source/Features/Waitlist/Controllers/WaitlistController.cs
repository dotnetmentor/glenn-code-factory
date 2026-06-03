using System.ComponentModel.DataAnnotations;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Source.Features.Waitlist.Commands;
using Source.Features.Waitlist.Queries;
using Source.Infrastructure.AuthorizationModels;
using Source.Shared.Controllers;

namespace Source.Features.Waitlist.Controllers;

/// <summary>
/// Public waitlist endpoint behind the marketing landing page, plus a super-admin
/// read view. The POST is anonymous, size-capped and rate-limited because it's
/// exposed to the open internet; the GET requires SuperAdmin.
/// </summary>
[Route("api/waitlist")]
[Tags("Waitlist")]
public class WaitlistController : BaseApiController
{
    public WaitlistController(IMediator mediator, ILogger<WaitlistController> logger)
        : base(mediator, logger)
    {
    }

    /// <summary>
    /// Join the waitlist. Anonymous + idempotent: a repeat email returns success
    /// silently. UserAgent/Referrer are captured server-side (never trusted from body).
    /// </summary>
    [HttpPost]
    [AllowAnonymous]
    [RequestSizeLimit(8192)]
    [EnableRateLimiting("Waitlist")]
    [ProducesResponseType<CreateWaitlistSignupResponse>(200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(429)]
    public async Task<ActionResult<CreateWaitlistSignupResponse>> Join([FromBody] JoinWaitlistRequest request)
    {
        var command = new CreateWaitlistSignupCommand
        {
            Email = request.Email,
            Source = request.Source,
            Note = request.Note,
            UserAgent = Request.Headers.UserAgent.ToString(),
            Referrer = Request.Headers.Referer.ToString(),
        };

        var result = await Mediator.Send(command);
        return HandleResult(result);
    }

    /// <summary>
    /// Super-admin: paged list of signups, newest first.
    /// </summary>
    [HttpGet]
    [Authorize(Roles = RoleConstants.SuperAdmin)]
    [EnableRateLimiting("GeneralPolicy")]
    [ProducesResponseType<GetAllWaitlistSignupsResponse>(200)]
    public async Task<ActionResult<GetAllWaitlistSignupsResponse>> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? search = null)
    {
        (page, pageSize) = ValidatePagination(page, pageSize);

        var result = await Mediator.Send(new GetAllWaitlistSignupsQuery
        {
            Page = page,
            PageSize = pageSize,
            Search = search,
        });

        return HandleResult(result);
    }
}

/// <summary>
/// Body for <see cref="WaitlistController.Join"/>. Every field is length-capped —
/// this endpoint is anonymous and public. <c>Severity</c>-style server fields
/// (UserAgent/Referrer) are intentionally absent; the server fills those in.
/// </summary>
public record JoinWaitlistRequest
{
    [Required]
    [EmailAddress]
    [StringLength(256)]
    public string Email { get; init; } = string.Empty;

    [StringLength(50)]
    public string? Source { get; init; }

    [StringLength(500)]
    public string? Note { get; init; }
}

using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Source.Features.Users.Models;
using Source.Shared.Controllers;
using System.Security.Claims;

namespace Source.Features.Me.Controllers;

[Route("api/me")]
[Authorize]
[EnableRateLimiting("GeneralPolicy")]
[Tags("Me")]
public class MeController : BaseApiController
{
    public MeController(IMediator mediator, ILogger<MeController> logger)
        : base(mediator, logger)
    {
    }

    /// <summary>
    /// Get the current authenticated user's profile.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<MeResponse>(200)]
    [ProducesResponseType(401)]
    public ActionResult<MeResponse> GetMe()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var email = User.FindFirstValue(ClaimTypes.Email);
        var firstName = User.FindFirstValue("firstName");
        var lastName = User.FindFirstValue("lastName");
        var roles = User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();

        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        return Ok(new MeResponse
        {
            Id = userId,
            Email = email ?? "",
            FirstName = firstName ?? "",
            LastName = lastName ?? "",
            Roles = roles
        });
    }
}

public record MeResponse
{
    public required string Id { get; init; }
    public required string Email { get; init; }
    public required string FirstName { get; init; }
    public required string LastName { get; init; }
    public required List<string> Roles { get; init; }
}

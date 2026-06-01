using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Source.Features.Users.Commands;
using Source.Features.Users.Queries;
using Source.Infrastructure.AuthorizationModels;
using Source.Infrastructure.AuthorizationExtensions;
using Source.Shared.Controllers;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;

namespace Source.Features.Users.Controllers;

[Route("api/[controller]")]
[Authorize]
[EnableRateLimiting("GeneralPolicy")]
public class UsersController : BaseApiController
{
    public UsersController(IMediator mediator, ILogger<UsersController> logger)
        : base(mediator, logger)
    {
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<UserResponse>> GetUser(string id)
    {
        var currentUserId = GetCurrentUserId();
        if (currentUserId != id && !User.HasSuperAdminPrivileges())
            return Forbid();

        var query = new GetUserQuery(id);
        var result = await Mediator.Send(query);

        return HandleResultWithNotFound(result);
    }

    [HttpGet("me")]
    public async Task<ActionResult<UserResponse>> GetCurrentUser()
    {
        var currentUserId = GetCurrentUserId();
        if (string.IsNullOrEmpty(currentUserId))
            return Unauthorized("Invalid token");

        var query = new GetUserQuery(currentUserId);
        var result = await Mediator.Send(query);

        return HandleResultWithNotFound(result);
    }

    [HttpGet]
    [Authorize(Roles = RoleConstants.SuperAdmin)]
    public async Task<ActionResult<GetAllUsersResponse>> GetAllUsers(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] string? search = null,
        [FromQuery] bool fetchAll = false)
    {
        (page, pageSize) = ValidatePagination(page, pageSize);

        var query = new GetAllUsersQuery
        {
            Page = page,
            PageSize = pageSize,
            Search = search,
            FetchAll = fetchAll
        };
        var result = await Mediator.Send(query);

        return HandleResult(result);
    }

    [HttpPost]
    [Authorize(Roles = RoleConstants.SuperAdmin)]
    [EnableRateLimiting("EmailPolicy")]
    public async Task<ActionResult<CreateUserResponse>> CreateUser([FromBody] CreateUserRequest request)
    {
        var command = new CreateUserCommand
        {
            Email = request.Email,
            FirstName = request.FirstName,
            LastName = request.LastName
        };
        var result = await Mediator.Send(command);

        return HandleCreatedResult(result, nameof(GetUser), new { id = result.IsSuccess ? result.Value.UserId : null });
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<UpdateUserResponse>> UpdateUser(string id, [FromBody] UpdateUserRequest request)
    {
        var currentUserId = GetCurrentUserId();

        if (currentUserId != id && !User.HasSuperAdminPrivileges())
            return Forbid();

        var command = new UpdateUserCommand
        {
            UserId = id,
            FirstName = request.FirstName,
            LastName = request.LastName,
            Email = request.Email,
            CurrentUser = User
        };
        var result = await Mediator.Send(command);

        return HandleResult(result);
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult<DeleteUserResponse>> DeleteUser(string id)
    {
        var currentUserId = GetCurrentUserId();
        if (currentUserId != id && !User.HasSuperAdminPrivileges())
            return Forbid();

        var command = new DeleteUserCommand(id);
        var result = await Mediator.Send(command);

        return HandleResult(result);
    }

    [HttpGet("{id}/roles")]
    public async Task<ActionResult<UserRolesResponse>> GetUserRoles(string id)
    {
        var currentUserId = GetCurrentUserId();

        if (currentUserId != id && !User.HasSuperAdminPrivileges())
            return Forbid();

        var query = new GetUserRolesQuery(id);
        var result = await Mediator.Send(query);

        return HandleResultWithNotFound(result);
    }

    [HttpPut("{id}/roles")]
    [Authorize(Roles = RoleConstants.SuperAdmin)]
    public async Task<ActionResult<UpdateUserRolesResponse>> UpdateUserRoles(string id, [FromBody] UpdateUserRolesRequest request)
    {
        var command = new UpdateUserRolesCommand
        {
            UserId = id,
            Roles = request.Roles ?? new List<string>()
        };
        var result = await Mediator.Send(command);

        return HandleResult(result);
    }
}

public class CreateUserRequest
{
    [Required]
    [EmailAddress]
    public required string Email { get; set; }

    [StringLength(100)]
    public string? FirstName { get; set; }

    [StringLength(100)]
    public string? LastName { get; set; }
}

public class UpdateUserRequest
{
    [StringLength(100)]
    public string? FirstName { get; set; }

    [StringLength(100)]
    public string? LastName { get; set; }

    [EmailAddress]
    public string? Email { get; set; }
}

public class UpdateUserRolesRequest
{
    public List<string>? Roles { get; set; }
}

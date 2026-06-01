using MediatR;
using Microsoft.AspNetCore.Mvc;
using Source.Shared.Results;
using System.Security.Claims;

namespace Source.Shared.Controllers;

/// <summary>
/// Base controller that provides common functionality for all API controllers
/// Reduces boilerplate by centralizing IMediator, logging, and result handling
/// </summary>
[ApiController]
public abstract class BaseApiController : ControllerBase
{
    protected readonly IMediator Mediator;
    protected readonly ILogger Logger;

    protected BaseApiController(IMediator mediator, ILogger logger)
    {
        Mediator = mediator;
        Logger = logger;
    }

    /// <summary>
    /// Gets the current authenticated user's ID from claims
    /// </summary>
    /// <returns>User ID if authenticated, null otherwise</returns>
    protected string? GetCurrentUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier);
    }

    /// <summary>
    /// Gets the current authenticated user's ID or returns Unauthorized if not found
    /// </summary>
    /// <returns>ActionResult with Unauthorized or the user ID</returns>
    protected ActionResult<string> GetCurrentUserIdOrUnauthorized()
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized(new { error = "User not authenticated" });
        }
        return userId;
    }

    /// <summary>
    /// Gets the current authenticated user's email from claims
    /// </summary>
    protected string? GetCurrentUserEmail()
    {
        return User.FindFirstValue(ClaimTypes.Email);
    }

    /// <summary>
    /// Converts a Result into an appropriate ActionResult
    /// Success -> Ok(200), Failure -> BadRequest(400)
    /// </summary>
    protected ActionResult<T> HandleResult<T>(Result<T> result)
    {
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        Logger.LogWarning("Request failed: {Error}", result.Error);
        return BadRequest(new { error = result.Error });
    }

    /// <summary>
    /// Converts a Result into an ActionResult with NotFound support
    /// Success -> Ok(200), "not found" errors -> NotFound(404), other failures -> BadRequest(400)
    /// </summary>
    protected ActionResult<T> HandleResultWithNotFound<T>(Result<T> result)
    {
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        if (result.Error?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true)
        {
            Logger.LogWarning("Resource not found: {Error}", result.Error);
            return NotFound(new { error = result.Error });
        }

        Logger.LogWarning("Request failed: {Error}", result.Error);
        return BadRequest(new { error = result.Error });
    }

    /// <summary>
    /// Converts a Result into a Created (201) response
    /// Used for POST endpoints that create new resources
    /// </summary>
    protected ActionResult<T> HandleCreatedResult<T>(Result<T> result, string actionName, object? routeValues = null)
    {
        if (result.IsSuccess)
        {
            return CreatedAtAction(actionName, routeValues, result.Value);
        }

        Logger.LogWarning("Creation failed: {Error}", result.Error);
        return BadRequest(new { error = result.Error });
    }

    /// <summary>
    /// Handles creation with conflict detection (409)
    /// Checks for "already exists" type errors
    /// </summary>
    protected ActionResult<T> HandleCreatedResultWithConflict<T>(Result<T> result, string actionName, object? routeValues = null)
    {
        if (result.IsSuccess)
        {
            return CreatedAtAction(actionName, routeValues, result.Value);
        }

        if (result.Error?.Contains("already exists", StringComparison.OrdinalIgnoreCase) == true ||
            result.Error?.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true)
        {
            Logger.LogWarning("Resource conflict: {Error}", result.Error);
            return Conflict(new { error = result.Error });
        }

        Logger.LogWarning("Creation failed: {Error}", result.Error);
        return BadRequest(new { error = result.Error });
    }

    /// <summary>
    /// Handles simple Result without value (for void operations)
    /// </summary>
    protected ActionResult HandleResult(Result result)
    {
        if (result.IsSuccess)
        {
            return Ok();
        }

        Logger.LogWarning("Request failed: {Error}", result.Error);
        return BadRequest(new { error = result.Error });
    }

    /// <summary>
    /// Validates pagination parameters and applies safe defaults
    /// </summary>
    protected (int page, int pageSize) ValidatePagination(int page, int pageSize)
    {
        if (page < 1) page = 1;
        return (page, pageSize);
    }
}

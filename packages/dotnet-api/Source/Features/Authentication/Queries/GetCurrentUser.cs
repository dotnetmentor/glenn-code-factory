using Microsoft.AspNetCore.Identity;
using Source.Features.Users.Models;
using Source.Infrastructure.AuthorizationModels;
using Source.Infrastructure.AuthorizationExtensions;
using Source.Shared.CQRS;
using Source.Shared.Results;
using System.Security.Claims;

namespace Source.Features.Authentication.Queries;

/// <summary>
/// Query to get current authenticated user
/// </summary>
public record GetCurrentUserQuery(ClaimsPrincipal User) : IQuery<Result<CurrentUserResponse>>;

/// <summary>
/// Response for current user information
/// </summary>
public record CurrentUserResponse
{
    public required string UserId { get; init; }
    public required string Email { get; init; }
    public required bool IsAuthenticated { get; init; }
    public required List<string> Roles { get; init; }
    public string? PhoneNumber { get; init; }
    public required bool IsOnboarded { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
}

/// <summary>
/// Handler for getting current user information
/// </summary>
public class GetCurrentUserHandler : IQueryHandler<GetCurrentUserQuery, Result<CurrentUserResponse>>
{
    private readonly UserManager<User> _userManager;
    private readonly ILogger<GetCurrentUserHandler> _logger;

    public GetCurrentUserHandler(UserManager<User> userManager, ILogger<GetCurrentUserHandler> logger)
    {
        _userManager = userManager;
        _logger = logger;
    }

    public async Task<Result<CurrentUserResponse>> Handle(GetCurrentUserQuery request, CancellationToken cancellationToken)
    {
        if (!request.User.Identity?.IsAuthenticated == true)
        {
            return Result.Success(new CurrentUserResponse
            {
                UserId = "",
                Email = "",
                IsAuthenticated = false,
                Roles = new List<string>(),
                IsOnboarded = false
            });
        }

        var userId = request.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("User is authenticated but no user ID claim found");
            return Result.Failure<CurrentUserResponse>("Invalid authentication token");
        }

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
        {
            _logger.LogWarning("User {UserId} not found in database", userId);
            return Result.Failure<CurrentUserResponse>("User not found");
        }

        // Get user roles as strings
        var roleNames = await _userManager.GetRolesAsync(user);

        _logger.LogInformation("Retrieved current user info for {Email} with roles: {Roles}", 
            user.Email, string.Join(", ", roleNames));

        var response = new CurrentUserResponse
        {
            UserId = user.Id,
            Email = user.Email ?? "",
            IsAuthenticated = true,
            Roles = roleNames.ToList(),
            PhoneNumber = user.PhoneNumber,
            IsOnboarded = user.IsOnboarded,
            FirstName = user.FirstName,
            LastName = user.LastName
        };
        return Result.Success(response);
    }
} 
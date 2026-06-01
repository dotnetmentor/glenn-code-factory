using Microsoft.AspNetCore.Identity;
using Source.Features.Users.Models;
using Source.Infrastructure.AuthorizationExtensions;
using Source.Shared.CQRS;
using Source.Shared.Results;
using System.Security.Claims;

namespace Source.Features.Users.Commands;

public record UpdateUserCommand : ICommand<Result<UpdateUserResponse>>
{
    public string UserId { get; init; } = string.Empty;
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Email { get; init; }
    public ClaimsPrincipal? CurrentUser { get; init; }
}

public record UpdateUserResponse
{
    public required string UserId { get; init; }
    public required string Email { get; init; }
    public required string FullName { get; init; }
    public required DateTime UpdatedAt { get; init; }
}

public class UpdateUserHandler : ICommandHandler<UpdateUserCommand, Result<UpdateUserResponse>>
{
    private readonly UserManager<User> _userManager;
    private readonly ILogger<UpdateUserHandler> _logger;

    public UpdateUserHandler(UserManager<User> userManager, ILogger<UpdateUserHandler> logger)
    {
        _userManager = userManager;
        _logger = logger;
    }

    public async Task<Result<UpdateUserResponse>> Handle(UpdateUserCommand request, CancellationToken cancellationToken)
    {
        var user = await _userManager.FindByIdAsync(request.UserId);
        if (user == null)
            return Result.Failure<UpdateUserResponse>("User not found");

        if (user.IsDeleted)
            return Result.Failure<UpdateUserResponse>("Cannot update deleted user");

        // Rich model handles profile fields + raises domain event
        var profileResult = user.UpdateProfile(request.FirstName, request.LastName);
        if (profileResult.IsFailure)
            return Result.Failure<UpdateUserResponse>(profileResult.Error!);

        // Email change stays in handler — needs async DB uniqueness check
        if (!string.IsNullOrWhiteSpace(request.Email) && request.Email != user.Email)
        {
            var emailExists = await _userManager.FindByEmailAsync(request.Email);
            if (emailExists != null && emailExists.Id != user.Id)
                return Result.Failure<UpdateUserResponse>("Email already in use");

            user.Email = request.Email;
            user.UserName = request.Email;
        }

        // UserManager.UpdateAsync calls SaveChanges → interceptor persists + dispatches events
        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            return Result.Failure<UpdateUserResponse>($"Update failed: {errors}");
        }

        _logger.LogInformation("Successfully updated user {UserId}", user.Id);

        var response = new UpdateUserResponse { UserId = user.Id, Email = user.Email!, FullName = user.FullName, UpdatedAt = user.UpdatedAt };
        return Result.Success(response);
    }
}

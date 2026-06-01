using Microsoft.AspNetCore.Identity;
using Source.Features.Users.Models;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Authentication.Commands;

/// <summary>
/// Command to change a user's existing password
/// </summary>
public record ChangePasswordCommand(string UserId, string CurrentPassword, string NewPassword, string ConfirmPassword)
    : ICommand<Result<ChangePasswordResponse>>;

/// <summary>
/// Response for password change
/// </summary>
public record ChangePasswordResponse
{
    public required string Message { get; init; }
}

/// <summary>
/// Handler for changing an existing password
/// </summary>
public class ChangePasswordHandler : ICommandHandler<ChangePasswordCommand, Result<ChangePasswordResponse>>
{
    private readonly UserManager<User> _userManager;
    private readonly ILogger<ChangePasswordHandler> _logger;

    public ChangePasswordHandler(UserManager<User> userManager, ILogger<ChangePasswordHandler> logger)
    {
        _userManager = userManager;
        _logger = logger;
    }

    public async Task<Result<ChangePasswordResponse>> Handle(ChangePasswordCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Attempting to change password for user: {UserId}", request.UserId);

        if (request.NewPassword != request.ConfirmPassword)
        {
            return Result.Failure<ChangePasswordResponse>("New passwords do not match");
        }

        var user = await _userManager.FindByIdAsync(request.UserId);
        if (user == null)
        {
            _logger.LogWarning("Change password attempted for non-existent user: {UserId}", request.UserId);
            return Result.Failure<ChangePasswordResponse>("User not found");
        }

        var result = await _userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            _logger.LogWarning("Change password failed for user {UserId}: {Errors}", request.UserId, errors);
            return Result.Failure<ChangePasswordResponse>($"Failed to change password: {errors}");
        }

        _logger.LogInformation("Password changed successfully for user: {UserId}", request.UserId);

        var response = new ChangePasswordResponse
        {
            Message = "Password has been changed successfully."
        };
        return Result.Success(response);
    }
}

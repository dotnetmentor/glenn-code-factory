using Microsoft.AspNetCore.Identity;
using Source.Features.Users.Models;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Authentication.Commands;

/// <summary>
/// Command to set a password for a user who does not have one (e.g., OTP-only users)
/// </summary>
public record SetPasswordCommand(string UserId, string NewPassword, string ConfirmPassword)
    : ICommand<Result<SetPasswordResponse>>;

/// <summary>
/// Response for setting a password
/// </summary>
public record SetPasswordResponse
{
    public required string Message { get; init; }
}

/// <summary>
/// Handler for setting password on an account that has no password yet
/// </summary>
public class SetPasswordHandler : ICommandHandler<SetPasswordCommand, Result<SetPasswordResponse>>
{
    private readonly UserManager<User> _userManager;
    private readonly ILogger<SetPasswordHandler> _logger;

    public SetPasswordHandler(UserManager<User> userManager, ILogger<SetPasswordHandler> logger)
    {
        _userManager = userManager;
        _logger = logger;
    }

    public async Task<Result<SetPasswordResponse>> Handle(SetPasswordCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Attempting to set password for user: {UserId}", request.UserId);

        if (request.NewPassword != request.ConfirmPassword)
        {
            return Result.Failure<SetPasswordResponse>("Passwords do not match");
        }

        var user = await _userManager.FindByIdAsync(request.UserId);
        if (user == null)
        {
            _logger.LogWarning("Set password attempted for non-existent user: {UserId}", request.UserId);
            return Result.Failure<SetPasswordResponse>("User not found");
        }

        var hasPassword = await _userManager.HasPasswordAsync(user);
        if (hasPassword)
        {
            _logger.LogWarning("Set password attempted for user who already has a password: {UserId}", request.UserId);
            return Result.Failure<SetPasswordResponse>("User already has a password. Use change password instead.");
        }

        var result = await _userManager.AddPasswordAsync(user, request.NewPassword);
        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            _logger.LogWarning("Set password failed for user {UserId}: {Errors}", request.UserId, errors);
            return Result.Failure<SetPasswordResponse>($"Failed to set password: {errors}");
        }

        _logger.LogInformation("Password set successfully for user: {UserId}", request.UserId);

        var response = new SetPasswordResponse
        {
            Message = "Password has been set successfully. You can now sign in with your password."
        };
        return Result.Success(response);
    }
}

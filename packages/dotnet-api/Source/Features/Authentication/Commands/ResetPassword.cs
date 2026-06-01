using Microsoft.AspNetCore.Identity;
using Source.Features.Users.Models;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Authentication.Commands;

/// <summary>
/// Command to reset a user's password using a reset token
/// </summary>
public record ResetPasswordCommand(string Email, string Token, string NewPassword, string ConfirmPassword)
    : ICommand<Result<ResetPasswordResponse>>;

/// <summary>
/// Response for password reset
/// </summary>
public record ResetPasswordResponse
{
    public required string Message { get; init; }
}

/// <summary>
/// Handler for resetting password via token
/// </summary>
public class ResetPasswordHandler : ICommandHandler<ResetPasswordCommand, Result<ResetPasswordResponse>>
{
    private readonly UserManager<User> _userManager;
    private readonly ILogger<ResetPasswordHandler> _logger;

    public ResetPasswordHandler(UserManager<User> userManager, ILogger<ResetPasswordHandler> logger)
    {
        _userManager = userManager;
        _logger = logger;
    }

    public async Task<Result<ResetPasswordResponse>> Handle(ResetPasswordCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Attempting password reset for email: {Email}", request.Email);

        if (request.NewPassword != request.ConfirmPassword)
        {
            return Result.Failure<ResetPasswordResponse>("Passwords do not match");
        }

        var user = await _userManager.FindByEmailAsync(request.Email);
        if (user == null)
        {
            _logger.LogWarning("Password reset attempted for non-existent email: {Email}", request.Email);
            return Result.Failure<ResetPasswordResponse>("Invalid or expired reset token");
        }

        var result = await _userManager.ResetPasswordAsync(user, request.Token, request.NewPassword);
        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            _logger.LogWarning("Password reset failed for {Email}: {Errors}", request.Email, errors);
            return Result.Failure<ResetPasswordResponse>($"Password reset failed: {errors}");
        }

        _logger.LogInformation("Password reset successful for: {Email}", request.Email);

        var response = new ResetPasswordResponse
        {
            Message = "Password has been reset successfully. You can now sign in with your new password."
        };
        return Result.Success(response);
    }
}

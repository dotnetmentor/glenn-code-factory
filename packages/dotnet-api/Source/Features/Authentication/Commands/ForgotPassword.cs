using Microsoft.AspNetCore.Identity;
using Source.Features.Users.Models;
using Source.Infrastructure.Services.Email;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Authentication.Commands;

/// <summary>
/// Command to initiate a password reset flow by sending a reset link email
/// </summary>
public record ForgotPasswordCommand(string Email) : ICommand<Result<ForgotPasswordResponse>>;

/// <summary>
/// Response for forgot password request. Always succeeds to avoid leaking email existence.
/// </summary>
public record ForgotPasswordResponse
{
    public required string Message { get; init; }
}

/// <summary>
/// Handler for forgot password - generates reset token and sends email
/// </summary>
public class ForgotPasswordHandler : ICommandHandler<ForgotPasswordCommand, Result<ForgotPasswordResponse>>
{
    private readonly UserManager<User> _userManager;
    private readonly IEmailService _emailService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ForgotPasswordHandler> _logger;

    public ForgotPasswordHandler(
        UserManager<User> userManager,
        IEmailService emailService,
        IConfiguration configuration,
        ILogger<ForgotPasswordHandler> logger)
    {
        _userManager = userManager;
        _emailService = emailService;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<Result<ForgotPasswordResponse>> Handle(ForgotPasswordCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Password reset requested for email: {Email}", request.Email);

        var successResponse = new ForgotPasswordResponse
        {
            Message = "If an account with that email exists, a password reset link has been sent."
        };

        var user = await _userManager.FindByEmailAsync(request.Email);
        if (user == null)
        {
            // Do not reveal that the user does not exist
            _logger.LogWarning("Password reset requested for non-existent email: {Email}", request.Email);
            return Result.Success(successResponse);
        }

        var token = await _userManager.GeneratePasswordResetTokenAsync(user);
        var encodedToken = Uri.EscapeDataString(token);
        var encodedEmail = Uri.EscapeDataString(request.Email);
        var resetLink = $"/reset-password?token={encodedToken}&email={encodedEmail}";

        // Try to build a full URL if a base URL is configured
        var baseUrl = _configuration["App:FrontendBaseUrl"];
        if (!string.IsNullOrEmpty(baseUrl))
        {
            resetLink = $"{baseUrl.TrimEnd('/')}/reset-password?token={encodedToken}&email={encodedEmail}";
        }

        try
        {
            var emailMessage = new EmailMessage(
                To: user.Email!,
                Subject: "Reset Your Password",
                HtmlBody: $@"
                <div style='font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;'>
                    <h2 style='color: #333; text-align: center;'>Reset Your Password</h2>
                    <p style='color: #666; text-align: center;'>
                        You requested a password reset. Click the button below to set a new password.
                    </p>
                    <div style='text-align: center; margin: 30px 0;'>
                        <a href='{resetLink}' style='background: #007bff; color: white; padding: 12px 24px; text-decoration: none; border-radius: 6px; font-size: 16px;'>
                            Reset Password
                        </a>
                    </div>
                    <p style='color: #999; text-align: center; font-size: 14px;'>
                        If you didn't request this, you can safely ignore this email.
                    </p>
                </div>",
                TextBody: $"Reset your password by visiting this link: {resetLink}\n\nIf you didn't request this, you can safely ignore this email."
            );

            await _emailService.SendEmailAsync(emailMessage, cancellationToken);
            _logger.LogInformation("Password reset email sent to: {Email}", request.Email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send password reset email to: {Email}", request.Email);
            // Still return success to avoid leaking email existence
        }

        return Result.Success(successResponse);
    }
}

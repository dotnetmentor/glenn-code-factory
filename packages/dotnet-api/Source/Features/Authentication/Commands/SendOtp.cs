using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Hosting;
using Source.Features.Users.Models;
using Source.Infrastructure.Services.Email;
using Source.Shared.CQRS;
using Source.Shared.Results;
using System.Security.Cryptography;

namespace Source.Features.Authentication.Commands;

/// <summary>
/// Command to send OTP code to user's email
/// </summary>
public record SendOtpCommand(string Email) : ICommand<Result<SendOtpResponse>>;

/// <summary>
/// Response for OTP send request
/// </summary>
public record SendOtpResponse
{
    public required string Message { get; init; }
    public required DateTime ExpiresAt { get; init; }
}

/// <summary>
/// Handler for sending OTP codes
/// </summary>
public class SendOtpHandler : ICommandHandler<SendOtpCommand, Result<SendOtpResponse>>
{
    private const string TestEmail = "test@test.com";
    private const string TestOtp = "123456";
    private readonly UserManager<User> _userManager;
    private readonly IEmailService _emailService;
    private readonly ILogger<SendOtpHandler> _logger;
    private readonly IWebHostEnvironment _environment;

    public SendOtpHandler(
        UserManager<User> userManager,
        IEmailService emailService,
        ILogger<SendOtpHandler> logger,
        IWebHostEnvironment environment)
    {
        _userManager = userManager;
        _emailService = emailService;
        _logger = logger;
        _environment = environment;
    }

    public async Task<Result<SendOtpResponse>> Handle(SendOtpCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("🔐 Sending OTP to email: {Email}", request.Email);

        var isDevTestUser = request.Email == TestEmail && _environment.IsDevelopment();

        // Find the user. Self-registration is CLOSED: accounts must be provisioned
        // by an administrator (or created by the startup SuperAdmin seeder). The only
        // exception is the dev test user, which is auto-created in Development so local
        // testing stays frictionless.
        var user = await _userManager.FindByEmailAsync(request.Email);
        if (user == null)
        {
            if (!isDevTestUser)
            {
                _logger.LogWarning("🚫 OTP requested for a non-existent account: {Email}", request.Email);
                return Result.Failure<SendOtpResponse>(
                    "No account found for this email address. Please contact your administrator for access.");
            }

            // Dev-only: auto-create the local test user with SuperAdmin access.
            user = new User
            {
                UserName = request.Email,
                Email = request.Email,
                EmailConfirmed = true,
                Credits = 500
            };

            var result = await _userManager.CreateAsync(user);
            if (!result.Succeeded)
            {
                var errors = string.Join(", ", result.Errors.Select(e => e.Description));
                _logger.LogWarning("Failed to create user {Email}: {Errors}", request.Email, errors);
                return Result.Failure<SendOtpResponse>($"Failed to create user: {errors}");
            }

            _logger.LogInformation("✅ Created dev test user for email: {Email}", request.Email);

            var roleResult = await _userManager.AddToRoleAsync(user, "SuperAdmin");
            if (roleResult.Succeeded)
            {
                _logger.LogInformation("✅ Assigned SuperAdmin role to test user: {Email}", request.Email);
            }
            else
            {
                _logger.LogWarning("⚠️ Failed to assign SuperAdmin role to test user: {Email}", request.Email);
            }
        }
        else if (isDevTestUser)
        {
            // Ensure test user has SuperAdmin role even if user already exists
            if (!await _userManager.IsInRoleAsync(user, "SuperAdmin"))
            {
                var roleResult = await _userManager.AddToRoleAsync(user, "SuperAdmin");
                if (roleResult.Succeeded)
                {
                    _logger.LogInformation("✅ Assigned SuperAdmin role to existing test user: {Email}", request.Email);
                }
            }
        }

        // Check rate limiting (skip for dev test user)
        if (!isDevTestUser && !user.CanRequestOtp())
        {
            _logger.LogWarning("⚠️ OTP rate limit exceeded for user: {Email}", request.Email);
            return Result.Failure<SendOtpResponse>("Please wait before requesting another OTP code");
        }

        // Generate secure OTP (use static OTP for dev test user)
        var otpCode = isDevTestUser ? TestOtp : GenerateOtpCode();
        var expiresAt = DateTime.UtcNow.AddMinutes(10); // 10-minute expiry

        // Update user with OTP
        user.OtpCode = otpCode;
        user.OtpExpiresAt = expiresAt;
        user.OtpAttempts = 0;
        user.LastOtpSentAt = DateTime.UtcNow;

        var updateResult = await _userManager.UpdateAsync(user);
        if (!updateResult.Succeeded)
        {
            _logger.LogError("Failed to update user with OTP: {Email}", request.Email);
            return Result.Failure<SendOtpResponse>("Failed to generate OTP");
        }

        // Skip sending email for dev test user
        if (isDevTestUser)
        {
            _logger.LogInformation("🔐 [DEV TEST] Using static OTP for test email: {Email} - OTP: {OtpCode}", request.Email, otpCode);
            var testResponse = new SendOtpResponse { Message = "OTP sent to your email", ExpiresAt = expiresAt };
            return Result.Success(testResponse);
        }

        // Send email
        try
        {
            var emailMessage = new EmailMessage(
                To: user.Email!,
                Subject: "Your Login Code",
                HtmlBody: $@"
                <div style='font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;'>
                    <h2 style='color: #333; text-align: center;'>Your Login Code</h2>
                    <div style='background: #f8f9fa; padding: 20px; border-radius: 8px; text-align: center; margin: 20px 0;'>
                        <h1 style='font-size: 32px; letter-spacing: 8px; color: #007bff; margin: 0;'>{otpCode}</h1>
                    </div>
                    <p style='color: #666; text-align: center;'>
                        This code will expire in 10 minutes.<br>
                        If you didn't request this code, please ignore this email.
                    </p>
                </div>",
                TextBody: $"Your login code is: {otpCode}\n\nThis code will expire in 10 minutes."
            );

            // In development, log OTP to console for testing
            if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development")
            {
                _logger.LogInformation("🔐 [DEV] OTP Code for {Email}: {OtpCode}", request.Email, otpCode);
            }

            await _emailService.SendEmailAsync(emailMessage, cancellationToken);

            _logger.LogInformation("📧 OTP sent successfully to: {Email}", request.Email);

            var response = new SendOtpResponse { Message = "OTP sent to your email", ExpiresAt = expiresAt };
            return Result.Success(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to send OTP email to: {Email}", request.Email);
            
            // Clear OTP on email failure
            user.ClearOtp();
            await _userManager.UpdateAsync(user);
            
            return Result.Failure<SendOtpResponse>("Failed to send OTP email");
        }
    }

    private static string GenerateOtpCode()
    {
        // Generate 6-digit secure random OTP
        using var rng = RandomNumberGenerator.Create();
        var bytes = new byte[4];
        rng.GetBytes(bytes);
        var randomNumber = Math.Abs(BitConverter.ToInt32(bytes, 0));
        return (randomNumber % 1000000).ToString("D6");
    }
} 
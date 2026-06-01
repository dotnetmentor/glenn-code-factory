using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Hosting;
using Source.Features.Authentication.Services;
using Source.Features.Users.Models;
using Source.Infrastructure.DevSeed;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Authentication.Commands;

/// <summary>
/// Command to verify OTP code and generate JWT token
/// </summary>
public record VerifyOtpCommand(string Email, string OtpCode) : ICommand<Result<VerifyOtpResponse>>;

/// <summary>
/// Response for OTP verification
/// </summary>
public record VerifyOtpResponse
{
    public required string Token { get; init; }
    public required string Email { get; init; }
    public required DateTime ExpiresAt { get; init; }
}

/// <summary>
/// Handler for OTP verification
/// </summary>
public class VerifyOtpHandler : ICommandHandler<VerifyOtpCommand, Result<VerifyOtpResponse>>
{
    private const string AppReviewPhoneNumber = "+4613371337";
    private const string AppReviewStaticOtp = "123456";
    private const string TestEmail = "test@test.com";
    private const string TestOtp = "123456";
    private readonly UserManager<User> _userManager;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly ILogger<VerifyOtpHandler> _logger;
    private readonly IWebHostEnvironment _environment;

    public VerifyOtpHandler(
        UserManager<User> userManager,
        IJwtTokenService jwtTokenService,
        ILogger<VerifyOtpHandler> logger,
        IWebHostEnvironment environment)
    {
        _userManager = userManager;
        _jwtTokenService = jwtTokenService;
        _logger = logger;
        _environment = environment;
    }

    public async Task<Result<VerifyOtpResponse>> Handle(VerifyOtpCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("🔐 Verifying OTP for email: {Email}", request.Email);

        var isDevTestUser = request.Email == TestEmail && _environment.IsDevelopment();

        // Check if this is a dev seed test user with hardcoded OTP
        var devSeedUser = _environment.IsDevelopment()
            ? DevSeedData.TestUsers.FirstOrDefault(u => u.Email.Equals(request.Email, StringComparison.OrdinalIgnoreCase))
            : null;
        var isDevSeedUser = devSeedUser != null;

        var user = await _userManager.FindByEmailAsync(request.Email);
        if (user == null)
        {
            _logger.LogWarning("⚠️ OTP verification failed: User not found for email {Email}", request.Email);
            return Result.Failure<VerifyOtpResponse>("Invalid email or OTP code");
        }

        // Check attempt limit (max 5 attempts) - skip for dev test users
        if (!isDevTestUser && !isDevSeedUser && user.OtpAttempts >= 5)
        {
            _logger.LogWarning("⚠️ OTP verification failed: Too many attempts for email {Email}", request.Email);
            user.ClearOtp();
            await _userManager.UpdateAsync(user);
            return Result.Failure<VerifyOtpResponse>("Too many attempts. Please request a new OTP code");
        }

        // Dev test email: Accept static OTP only in development
        // Dev seed users: Accept their hardcoded OTP (for E2E testing)
        // App Store review user: Accept static OTP "123456" for app approval process
        bool isOtpValid;
        if (isDevTestUser)
        {
            isOtpValid = request.OtpCode == TestOtp;
        }
        else if (isDevSeedUser)
        {
            // Dev seed users can use their hardcoded OTP (no send-otp needed)
            isOtpValid = request.OtpCode == devSeedUser!.OtpCode;
            if (isOtpValid)
            {
                _logger.LogInformation("🧪 Dev seed user {Email} authenticated with hardcoded OTP", request.Email);
            }
        }
        else if (user.PhoneNumber == AppReviewPhoneNumber)
        {
            isOtpValid = request.OtpCode == AppReviewStaticOtp && user.OtpExpiresAt > DateTime.UtcNow;
        }
        else
        {
            isOtpValid = user.IsOtpValid(request.OtpCode);
        }

        if (!isOtpValid)
        {
            user.OtpAttempts++;
            await _userManager.UpdateAsync(user);
            
            _logger.LogWarning("⚠️ OTP verification failed: Invalid code for email {Email} (Attempt {Attempts})", 
                request.Email, user.OtpAttempts);
            
            if (user.OtpAttempts >= 5)
            {
                user.ClearOtp();
                await _userManager.UpdateAsync(user);
                return Result.Failure<VerifyOtpResponse>("Too many invalid attempts. Please request a new OTP code");
            }
            
            return Result.Failure<VerifyOtpResponse>("Invalid OTP code");
        }

        // Clear OTP after successful verification
        user.ClearOtp();
        user.EmailConfirmed = true; // Confirm email on successful OTP
        var updateResult = await _userManager.UpdateAsync(user);
        
        if (!updateResult.Succeeded)
        {
            _logger.LogError("Failed to update user after OTP verification: {Email}", request.Email);
            return Result.Failure<VerifyOtpResponse>("Authentication failed");
        }

        // Generate JWT token using centralized service
        var (token, expiresAt) = await _jwtTokenService.GenerateTokenWithExpiryAsync(user, "otp");

        _logger.LogInformation("✅ OTP verified successfully for user: {Email}", request.Email);

        var response = new VerifyOtpResponse { Token = token, Email = user.Email!, ExpiresAt = expiresAt };
        return Result.Success(response);
    }
} 
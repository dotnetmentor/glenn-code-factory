using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Source.Features.Authentication.Commands;
using Source.Features.Authentication.Queries;
using Source.Features.Authentication.Services;
using Source.Features.Users.Models;
using Source.Shared.Controllers;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;

namespace Source.Features.Authentication.Controllers;

[Route("api/[controller]")]
[Tags("Authentication")]
public class AuthController : BaseApiController
{
    private readonly IWebHostEnvironment _environment;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly UserManager<User> _userManager;

    public AuthController(
        IMediator mediator,
        ILogger<AuthController> logger,
        IWebHostEnvironment environment,
        IJwtTokenService jwtTokenService,
        UserManager<User> userManager)
        : base(mediator, logger)
    {
        _environment = environment;
        _jwtTokenService = jwtTokenService;
        _userManager = userManager;
    }

    [HttpPost("register")]
    [EnableRateLimiting("EmailPolicy")]
    [ProducesResponseType<AuthCookieResponse>(200)]
    [ProducesResponseType(400)]
    public async Task<ActionResult<AuthCookieResponse>> Register([FromBody] RegisterRequest request)
    {
        var command = new RegisterUserCommand(request.Email, request.Password);
        var result = await Mediator.Send(command);

        if (!result.IsSuccess)
        {
            Logger.LogWarning("Registration failed for {Email}: {Error}", request.Email, result.Error);
            return BadRequest(new { error = result.Error });
        }

        Logger.LogInformation("User registered successfully: {Email}", request.Email);

        // Auto-login: find user, generate JWT, set cookie
        var user = await _userManager.FindByEmailAsync(request.Email);
        if (user == null)
        {
            Logger.LogError("User not found after registration: {Email}", request.Email);
            return BadRequest(new { error = "Registration succeeded but auto-login failed" });
        }

        var (token, expiresAt) = await _jwtTokenService.GenerateTokenWithExpiryAsync(user, "password");

        var cookieOptions = new CookieOptions
        {
            HttpOnly = true,
            Secure = _environment.IsDevelopment() || Request.IsHttps,
            SameSite = _environment.IsDevelopment() ? SameSiteMode.None : SameSiteMode.Lax,
            Expires = expiresAt,
            Path = "/"
        };

        Response.Cookies.Append("auth-token", token, cookieOptions);

        Logger.LogInformation("Auth cookie set after registration for: {Email}", request.Email);

        var response = new AuthCookieResponse { Message = "Registration successful", Email = request.Email };
        return Ok(response);
    }

    [HttpPost("login")]
    [EnableRateLimiting("AuthPolicy")]
    [ProducesResponseType<AuthCookieResponse>(200)]
    [ProducesResponseType(400)]
    public async Task<ActionResult<AuthCookieResponse>> Login([FromBody] LoginRequest request)
    {
        var command = new LoginUserCommand(request.Email, request.Password);
        var result = await Mediator.Send(command);

        if (!result.IsSuccess)
        {
            Logger.LogWarning("Login failed for {Email}: {Error}", request.Email, result.Error);
            return BadRequest(new { error = result.Error });
        }

        Logger.LogInformation("User logged in successfully: {Email}", request.Email);

        var cookieOptions = new CookieOptions
        {
            HttpOnly = true,
            Secure = _environment.IsDevelopment() || Request.IsHttps,
            SameSite = _environment.IsDevelopment() ? SameSiteMode.None : SameSiteMode.Lax,
            Expires = result.Value.ExpiresAt,
            Path = "/"
        };

        Response.Cookies.Append("auth-token", result.Value.Token, cookieOptions);

        Logger.LogInformation("Auth cookie set after login for: {Email}", request.Email);

        var response = new AuthCookieResponse { Message = "Authentication successful", Email = result.Value.Email };
        return Ok(response);
    }

    [HttpPost("send-otp")]
    [EnableRateLimiting("EmailPolicy")]
    [ProducesResponseType<SendOtpResponse>(200)]
    [ProducesResponseType(400)]
    public async Task<ActionResult<SendOtpResponse>> SendOtp([FromBody] SendOtpRequest request)
    {
        var command = new SendOtpCommand(request.Email);
        var result = await Mediator.Send(command);

        if (result.IsSuccess)
        {
            Logger.LogInformation("OTP sent successfully to: {Email}", request.Email);
        }

        return HandleResult(result);
    }

    [HttpPost("verify-otp")]
    [EnableRateLimiting("AuthPolicy")]
    [ProducesResponseType<VerifyOtpCookieResponse>(200)]
    [ProducesResponseType(400)]
    public async Task<ActionResult<VerifyOtpCookieResponse>> VerifyOtp([FromBody] VerifyOtpRequest request)
    {
        var command = new VerifyOtpCommand(request.Email, request.OtpCode);
        var result = await Mediator.Send(command);

        if (!result.IsSuccess)
        {
            Logger.LogWarning("OTP verification failed for {Email}: {Error}", request.Email, result.Error);
            return BadRequest(new { error = result.Error });
        }

        var cookieOptions = new CookieOptions
        {
            HttpOnly = true,
            Secure = _environment.IsDevelopment() || Request.IsHttps,
            SameSite = _environment.IsDevelopment() ? SameSiteMode.None : SameSiteMode.Lax,
            Expires = result.Value.ExpiresAt,
            Path = "/"
        };

        Response.Cookies.Append("auth-token", result.Value.Token, cookieOptions);

        Logger.LogInformation("OTP verified and auth cookie set for: {Email}", request.Email);

        var response = new VerifyOtpCookieResponse { Message = "Authentication successful", Email = result.Value.Email };
        return Ok(response);
    }

    [HttpPost("forgot-password")]
    [EnableRateLimiting("EmailPolicy")]
    [ProducesResponseType<ForgotPasswordResponse>(200)]
    [ProducesResponseType(400)]
    public async Task<ActionResult<ForgotPasswordResponse>> ForgotPassword([FromBody] ForgotPasswordRequest request)
    {
        var command = new ForgotPasswordCommand(request.Email);
        var result = await Mediator.Send(command);

        return HandleResult(result);
    }

    [HttpPost("reset-password")]
    [EnableRateLimiting("AuthPolicy")]
    [ProducesResponseType<ResetPasswordResponse>(200)]
    [ProducesResponseType(400)]
    public async Task<ActionResult<ResetPasswordResponse>> ResetPassword([FromBody] ResetPasswordRequest request)
    {
        var command = new ResetPasswordCommand(request.Email, request.Token, request.NewPassword, request.ConfirmPassword);
        var result = await Mediator.Send(command);

        return HandleResult(result);
    }

    [HttpPost("set-password")]
    [Authorize]
    [ProducesResponseType<SetPasswordResponse>(200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    public async Task<ActionResult<SetPasswordResponse>> SetPassword([FromBody] SetPasswordRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized(new { error = "User not authenticated" });
        }

        var command = new SetPasswordCommand(userId, request.NewPassword, request.ConfirmPassword);
        var result = await Mediator.Send(command);

        return HandleResult(result);
    }

    [HttpPost("change-password")]
    [Authorize]
    [ProducesResponseType<ChangePasswordResponse>(200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    public async Task<ActionResult<ChangePasswordResponse>> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized(new { error = "User not authenticated" });
        }

        var command = new ChangePasswordCommand(userId, request.CurrentPassword, request.NewPassword, request.ConfirmPassword);
        var result = await Mediator.Send(command);

        return HandleResult(result);
    }

    [HttpGet("has-password")]
    [Authorize]
    [ProducesResponseType<HasPasswordResponse>(200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    public async Task<ActionResult<HasPasswordResponse>> HasPassword()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized(new { error = "User not authenticated" });
        }

        var query = new HasPasswordQuery(userId);
        var result = await Mediator.Send(query);

        return HandleResult(result);
    }

    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType<CurrentUserResponse>(200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(400)]
    public async Task<ActionResult<CurrentUserResponse>> GetMe()
    {
        var query = new GetCurrentUserQuery(User);
        var result = await Mediator.Send(query);

        return HandleResult(result);
    }

    [HttpPost("logout")]
    [ProducesResponseType(200)]
    public ActionResult<object> Logout()
    {
        Response.Cookies.Delete("auth-token", new CookieOptions
        {
            HttpOnly = true,
            Secure = _environment.IsDevelopment() || Request.IsHttps,
            SameSite = _environment.IsDevelopment() ? SameSiteMode.None : SameSiteMode.Lax,
            Path = "/"
        });

        Logger.LogInformation("User logged out - auth cookie cleared");
        return Ok(new { message = "Logged out successfully" });
    }
}

// --- Request DTOs ---

public class RegisterRequest
{
    [Required]
    [EmailAddress]
    public required string Email { get; init; }

    [Required]
    [MinLength(6)]
    public required string Password { get; init; }
}

public class LoginRequest
{
    [Required]
    [EmailAddress]
    public required string Email { get; init; }

    [Required]
    public required string Password { get; init; }
}

public class SendOtpRequest
{
    [Required]
    [EmailAddress]
    public required string Email { get; init; }
}

public class VerifyOtpRequest
{
    [Required]
    [EmailAddress]
    public required string Email { get; init; }

    [Required]
    [StringLength(6, MinimumLength = 6, ErrorMessage = "OTP must be exactly 6 digits")]
    public required string OtpCode { get; init; }
}

public class ForgotPasswordRequest
{
    [Required]
    [EmailAddress]
    public required string Email { get; init; }
}

public class ResetPasswordRequest
{
    [Required]
    [EmailAddress]
    public required string Email { get; init; }

    [Required]
    public required string Token { get; init; }

    [Required]
    [MinLength(6)]
    public required string NewPassword { get; init; }

    [Required]
    [MinLength(6)]
    public required string ConfirmPassword { get; init; }
}

public class SetPasswordRequest
{
    [Required]
    [MinLength(6)]
    public required string NewPassword { get; init; }

    [Required]
    [MinLength(6)]
    public required string ConfirmPassword { get; init; }
}

public class ChangePasswordRequest
{
    [Required]
    public required string CurrentPassword { get; init; }

    [Required]
    [MinLength(6)]
    public required string NewPassword { get; init; }

    [Required]
    [MinLength(6)]
    public required string ConfirmPassword { get; init; }
}

// --- Response DTOs ---

public record AuthCookieResponse
{
    public required string Message { get; init; }
    public required string Email { get; init; }
}

public record VerifyOtpCookieResponse
{
    public required string Message { get; init; }
    public required string Email { get; init; }
}

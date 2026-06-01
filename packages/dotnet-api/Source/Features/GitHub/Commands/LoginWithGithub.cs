using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Source.Features.GitHub.Models;
using Source.Features.GitHub.Services;
using Source.Features.GitHub.Services.Dtos;
using Source.Features.Authentication.Services;
using Source.Features.Users.Events;
using Source.Features.Users.Models;
using Source.Features.Workspaces.Commands;
using Source.Infrastructure;
using Source.Infrastructure.AuthorizationModels;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.GitHub.Commands;

/// <summary>
/// Resolve a GitHub OAuth code into an application user (creating + auto-bootstrapping a workspace
/// if this is a new sign-in) and mint a JWT for cookie auth. The controller layer is responsible
/// for setting the resulting cookie and issuing the HTTP redirect.
/// </summary>
public record LoginWithGithubCommand(string Code, string? RedirectTo) : ICommand<Result<LoginWithGithubResponse>>;

public record LoginWithGithubResponse
{
    public required string UserId { get; init; }
    public required string Email { get; init; }
    public required IReadOnlyList<string> Roles { get; init; }
    public required string AuthToken { get; init; }
    public required DateTime AuthTokenExpiresAt { get; init; }
    public required string RedirectTo { get; init; }
}

public sealed class LoginWithGithubHandler : ICommandHandler<LoginWithGithubCommand, Result<LoginWithGithubResponse>>
{
    private readonly IGithubApiClient _ghApi;
    private readonly UserManager<User> _userManager;
    private readonly ApplicationDbContext _db;
    private readonly IMediator _mediator;
    private readonly IJwtTokenService _jwt;
    private readonly ILogger<LoginWithGithubHandler> _logger;

    public LoginWithGithubHandler(
        IGithubApiClient ghApi,
        UserManager<User> userManager,
        ApplicationDbContext db,
        IMediator mediator,
        IJwtTokenService jwt,
        ILogger<LoginWithGithubHandler> logger)
    {
        _ghApi = ghApi;
        _userManager = userManager;
        _db = db;
        _mediator = mediator;
        _jwt = jwt;
        _logger = logger;
    }

    public async Task<Result<LoginWithGithubResponse>> Handle(LoginWithGithubCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Code))
            return Result.Failure<LoginWithGithubResponse>("Missing OAuth code");

        // 1. Exchange code → access token.
        string accessToken;
        try
        {
            accessToken = await _ghApi.ExchangeOAuthCodeAsync(request.Code, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GitHub OAuth code exchange failed");
            return Result.Failure<LoginWithGithubResponse>("GitHub did not return an access token");
        }

        if (string.IsNullOrWhiteSpace(accessToken))
            return Result.Failure<LoginWithGithubResponse>("GitHub did not return an access token");

        // 2. Fetch the GitHub user identity.
        GithubUserDto ghUser;
        try
        {
            ghUser = await _ghApi.GetCurrentUserAsync(accessToken, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GitHub /user lookup failed");
            return Result.Failure<LoginWithGithubResponse>("Could not load GitHub profile");
        }

        if (ghUser is null || ghUser.Id <= 0 || string.IsNullOrWhiteSpace(ghUser.Login))
            return Result.Failure<LoginWithGithubResponse>("GitHub returned an invalid user profile");

        // 3. Determine email — fall back to /user/emails when the profile does not expose one.
        var email = ghUser.Email;
        if (string.IsNullOrWhiteSpace(email))
        {
            try
            {
                var emails = await _ghApi.GetCurrentUserEmailsAsync(accessToken, cancellationToken);
                var primary = emails.FirstOrDefault(e => e.Primary && e.Verified);
                email = primary?.Email;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GitHub /user/emails lookup failed");
            }
        }

        if (string.IsNullOrWhiteSpace(email))
            return Result.Failure<LoginWithGithubResponse>("Could not determine GitHub email — make sure your primary email is verified");

        // 4. Three-branch user resolution.
        User? user;

        // (A) Already linked.
        var existingIdentity = await _db.GithubUserIdentities
            .FirstOrDefaultAsync(i => i.GithubUserId == ghUser.Id, cancellationToken);
        if (existingIdentity is not null)
        {
            user = await _userManager.FindByIdAsync(existingIdentity.UserId);
            if (user is null)
            {
                _logger.LogError("GithubUserIdentity {Id} points at missing user {UserId}", existingIdentity.Id, existingIdentity.UserId);
                return Result.Failure<LoginWithGithubResponse>("Linked account is unavailable");
            }

            var dirty = false;
            if (existingIdentity.GithubLogin != ghUser.Login)
            {
                existingIdentity.GithubLogin = ghUser.Login;
                dirty = true;
            }
            if (existingIdentity.AvatarUrl != ghUser.AvatarUrl)
            {
                existingIdentity.AvatarUrl = ghUser.AvatarUrl;
                dirty = true;
            }
            if (dirty) await _db.SaveChangesAsync(cancellationToken);
        }
        else
        {
            // (B) Existing app user with this email — link.
            user = await _userManager.FindByEmailAsync(email);
            if (user is not null)
            {
                _db.GithubUserIdentities.Add(new GithubUserIdentity
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    GithubUserId = ghUser.Id,
                    GithubLogin = ghUser.Login,
                    AvatarUrl = ghUser.AvatarUrl,
                });

                // Defensively grant the WorkspaceUser app role if missing — legacy users may pre-date it.
                if (!await _userManager.IsInRoleAsync(user, RoleConstants.WorkspaceUser))
                {
                    var roleResult = await _userManager.AddToRoleAsync(user, RoleConstants.WorkspaceUser);
                    if (!roleResult.Succeeded)
                    {
                        _logger.LogWarning("Could not back-fill WorkspaceUser role for {UserId}: {Errors}",
                            user.Id, string.Join(", ", roleResult.Errors.Select(e => e.Description)));
                    }
                }

                await _db.SaveChangesAsync(cancellationToken);
            }
            else
            {
                // (C) Brand new user — passwordless signup.
                user = new User
                {
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                };

                var createResult = await _userManager.CreateAsync(user);
                if (!createResult.Succeeded)
                {
                    var errors = string.Join(", ", createResult.Errors.Select(e => e.Description));
                    _logger.LogWarning("Failed to create user from GitHub login {Email}: {Errors}", email, errors);
                    return Result.Failure<LoginWithGithubResponse>($"Could not create account: {errors}");
                }

                var roleResult = await _userManager.AddToRoleAsync(user, RoleConstants.WorkspaceUser);
                if (!roleResult.Succeeded)
                {
                    _logger.LogWarning("Could not assign {Role} to {UserId}: {Errors}",
                        RoleConstants.WorkspaceUser, user.Id,
                        string.Join(", ", roleResult.Errors.Select(e => e.Description)));
                    // Non-fatal — user can still sign in.
                }

                var workspaceResult = await _mediator.Send(
                    new CreateWorkspaceCommand(
                        OwnerUserId: user.Id,
                        Name: ghUser.Login,
                        Slug: null,
                        SlugSeed: ghUser.Login),
                    cancellationToken);
                if (!workspaceResult.IsSuccess)
                {
                    _logger.LogError("User {UserId} created but workspace bootstrap failed: {Error}",
                        user.Id, workspaceResult.Error);
                    return Result.Failure<LoginWithGithubResponse>(
                        $"Account created but workspace setup failed: {workspaceResult.Error}");
                }

                _db.GithubUserIdentities.Add(new GithubUserIdentity
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    GithubUserId = ghUser.Id,
                    GithubLogin = ghUser.Login,
                    AvatarUrl = ghUser.AvatarUrl,
                });
                await _db.SaveChangesAsync(cancellationToken);

                await _mediator.Publish(new UserCreated(user.Id, user.Email!, DateTime.UtcNow), cancellationToken);
            }
        }

        // 5. Issue auth cookie via JWT — same shape as /api/auth/login emits.
        var (token, expiresAt) = await _jwt.GenerateTokenWithExpiryAsync(user, "github");
        var roles = await _userManager.GetRolesAsync(user);

        // 6. Compute redirect — only allow safe relative paths.
        var redirect = SafeRelativeRedirect(request.RedirectTo);

        return Result.Success(new LoginWithGithubResponse
        {
            UserId = user.Id,
            Email = user.Email!,
            Roles = roles.ToList(),
            AuthToken = token,
            AuthTokenExpiresAt = expiresAt,
            RedirectTo = redirect,
        });
    }

    /// <summary>
    /// Open-redirect guard. Only accept relative paths starting with '/' (and not '//' which would
    /// be a protocol-relative URL like //evil.test/...).
    /// </summary>
    private static string SafeRelativeRedirect(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return "/";
        var trimmed = candidate.Trim();
        if (!trimmed.StartsWith('/')) return "/";
        if (trimmed.StartsWith("//")) return "/";
        return trimmed;
    }
}

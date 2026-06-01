using Microsoft.EntityFrameworkCore;
using Source.Features.GitHub.Services;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.GitHub.Commands;

/// <summary>
/// Handles the OAuth-only re-authorize callback. Anonymous endpoint — the workspace +
/// installation are recovered from the signed state token, not from the auth principal.
/// </summary>
public record HandleGithubUserAuthCallbackCommand(string? Code, string? StateToken)
    : ICommand<Result<HandleGithubUserAuthCallbackResponse>>;

public sealed record HandleGithubUserAuthCallbackResponse
{
    /// <summary>The workspace slug the user should be bounced back to.</summary>
    public required string WorkspaceSlug { get; init; }
    /// <summary>True iff the UAT was captured + persisted successfully.</summary>
    public required bool Success { get; init; }
}

public sealed class HandleGithubUserAuthCallbackHandler
    : ICommandHandler<HandleGithubUserAuthCallbackCommand, Result<HandleGithubUserAuthCallbackResponse>>
{
    private readonly ApplicationDbContext _db;
    private readonly IGithubInstallStateService _state;
    private readonly IGithubApiClient _api;
    private readonly ILogger<HandleGithubUserAuthCallbackHandler> _logger;

    public HandleGithubUserAuthCallbackHandler(
        ApplicationDbContext db,
        IGithubInstallStateService state,
        IGithubApiClient api,
        ILogger<HandleGithubUserAuthCallbackHandler> logger)
    {
        _db = db;
        _state = state;
        _api = api;
        _logger = logger;
    }

    public async Task<Result<HandleGithubUserAuthCallbackResponse>> Handle(
        HandleGithubUserAuthCallbackCommand request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.StateToken))
        {
            return Result.Failure<HandleGithubUserAuthCallbackResponse>("Invalid state");
        }

        var payload = _state.VerifyReauth(request.StateToken);
        if (payload is null)
        {
            return Result.Failure<HandleGithubUserAuthCallbackResponse>("Invalid or expired state");
        }

        var installation = await _db.GithubInstallations
            .FirstOrDefaultAsync(
                i => i.Id == payload.GithubInstallationId && i.WorkspaceId == payload.WorkspaceId,
                cancellationToken);
        if (installation is null)
        {
            return Result.Failure<HandleGithubUserAuthCallbackResponse>("Installation not found");
        }

        // Pull the workspace slug for the redirect target. Cheap projection — no need
        // to materialize the full Workspace row.
        var workspaceSlug = await _db.Workspaces
            .AsNoTracking()
            .Where(w => w.Id == payload.WorkspaceId)
            .Select(w => w.Slug)
            .FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(workspaceSlug))
        {
            return Result.Failure<HandleGithubUserAuthCallbackResponse>("Workspace not found");
        }

        if (string.IsNullOrWhiteSpace(request.Code))
        {
            // No code means GitHub didn't complete the OAuth side (user cancelled).
            // The caller bounces them back with ?reauth=error.
            _logger.LogInformation(
                "Reauth callback for installation {InstallationId} missing OAuth code; treating as cancelled",
                installation.InstallationId);
            return Result.Success(new HandleGithubUserAuthCallbackResponse
            {
                WorkspaceSlug = workspaceSlug,
                Success = false,
            });
        }

        try
        {
            var token = await _api.ExchangeOAuthCodeFullAsync(request.Code, cancellationToken);
            installation.UserAccessToken = token.AccessToken;
            installation.UserAccessTokenExpiresAt = token.AccessTokenExpiresAt;
            installation.UserRefreshToken = token.RefreshToken;
            installation.UserRefreshTokenExpiresAt = token.RefreshTokenExpiresAt;

            // Best-effort /user lookup so we keep UserLogin fresh. Not fatal if it fails.
            try
            {
                var ghUser = await _api.GetCurrentUserAsync(token.AccessToken, cancellationToken);
                installation.UserLogin = ghUser.Login;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Reauth captured UAT but /user lookup failed for installation {InstallationId}",
                    installation.InstallationId);
            }

            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Reauthorized GitHub UAT for installation {InstallationId} ({AccountLogin})",
                installation.InstallationId, installation.AccountLogin);

            return Result.Success(new HandleGithubUserAuthCallbackResponse
            {
                WorkspaceSlug = workspaceSlug,
                Success = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Reauth UAT exchange failed for installation {InstallationId}",
                installation.InstallationId);
            return Result.Success(new HandleGithubUserAuthCallbackResponse
            {
                WorkspaceSlug = workspaceSlug,
                Success = false,
            });
        }
    }
}

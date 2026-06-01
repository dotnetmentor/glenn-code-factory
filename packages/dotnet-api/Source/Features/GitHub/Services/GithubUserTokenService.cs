using Source.Features.GitHub.Models;
using Source.Infrastructure;

namespace Source.Features.GitHub.Services;

/// <summary>
/// Default <see cref="IGithubUserTokenService"/>. Keeps the refresh logic in one place
/// so handlers don't have to think about UAT lifecycles — they call this and either
/// get a valid token or a typed <see cref="GithubUserAuthRequiredException"/>.
/// </summary>
public sealed class GithubUserTokenService : IGithubUserTokenService
{
    /// <summary>
    /// Refresh window — once the current UAT is within this many minutes of expiry,
    /// we proactively swap it out via the refresh-token API. Two minutes gives
    /// callers enough headroom for a slow GitHub call to complete on the cached
    /// token if the refresh path hiccups.
    /// </summary>
    private static readonly TimeSpan RefreshWindow = TimeSpan.FromMinutes(2);

    private readonly ApplicationDbContext _db;
    private readonly IGithubApiClient _api;
    private readonly ILogger<GithubUserTokenService> _logger;

    public GithubUserTokenService(
        ApplicationDbContext db,
        IGithubApiClient api,
        ILogger<GithubUserTokenService> logger)
    {
        _db = db;
        _api = api;
        _logger = logger;
    }

    public async Task<string> GetValidUserAccessTokenAsync(GithubInstallation installation, CancellationToken ct)
    {
        if (installation is null) throw new ArgumentNullException(nameof(installation));

        // No UAT was ever captured — caller (and the frontend banner) treats this as
        // "user must complete the slim re-authorize flow".
        if (string.IsNullOrWhiteSpace(installation.UserAccessToken))
        {
            throw new GithubUserAuthRequiredException(GithubUserAuthRequiredException.Reason.NoUat);
        }

        var now = DateTime.UtcNow;

        // Happy path: UAT still has > 2min left. Hand it back as-is.
        if (installation.UserAccessTokenExpiresAt is { } accessExp && accessExp > now.Add(RefreshWindow))
        {
            return installation.UserAccessToken;
        }

        // Refresh path. If we don't even have a refresh token, or it's past its expiry,
        // there's nothing we can do automatically — the user has to re-authorize.
        if (string.IsNullOrWhiteSpace(installation.UserRefreshToken)
            || installation.UserRefreshTokenExpiresAt is null
            || installation.UserRefreshTokenExpiresAt <= now)
        {
            _logger.LogInformation(
                "UAT for installation {InstallationId} is expired and refresh token is missing/expired — re-auth required",
                installation.InstallationId);
            throw new GithubUserAuthRequiredException(GithubUserAuthRequiredException.Reason.RefreshExpired);
        }

        Dtos.GithubUserAccessTokenPayload payload;
        try
        {
            payload = await _api.RefreshUserAccessTokenAsync(installation.UserRefreshToken, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "UAT refresh API call failed for installation {InstallationId} — re-auth required",
                installation.InstallationId);
            throw new GithubUserAuthRequiredException(GithubUserAuthRequiredException.Reason.RefreshFailed, ex);
        }

        // GitHub rotates the refresh token on every exchange — capture the new one,
        // otherwise the next refresh will use a now-invalid value.
        installation.UserAccessToken = payload.AccessToken;
        installation.UserAccessTokenExpiresAt = payload.AccessTokenExpiresAt;
        if (!string.IsNullOrWhiteSpace(payload.RefreshToken))
        {
            installation.UserRefreshToken = payload.RefreshToken;
            installation.UserRefreshTokenExpiresAt = payload.RefreshTokenExpiresAt;
        }

        // Note: caller passes us a tracked entity (CreateProjectHandler loads the
        // installation row without AsNoTracking for this code path). If a caller
        // ever passes an untracked instance, Attach + state-tweak would be needed —
        // but the only consumer today does it right.
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Refreshed UAT for installation {InstallationId}; new expiry {Expiry:O}",
            installation.InstallationId, installation.UserAccessTokenExpiresAt);

        return payload.AccessToken;
    }
}

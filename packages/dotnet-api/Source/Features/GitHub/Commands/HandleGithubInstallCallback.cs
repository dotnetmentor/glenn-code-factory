using Microsoft.EntityFrameworkCore;
using Source.Features.GitHub.Models;
using Source.Features.GitHub.Services;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.GitHub.Commands;

/// <summary>
/// Handles the <c>GET /api/github/install/callback</c> redirect from GitHub. Anonymous endpoint —
/// the workspace is recovered from the signed state token, not from the route or auth principal.
/// </summary>
/// <param name="StateToken">Raw <c>state</c> query value.</param>
/// <param name="StateCookieValue">Raw <c>gh_install_state</c> cookie value (separate from <see cref="StateToken"/> so we can require both to match before proceeding).</param>
/// <param name="InstallationId">The GitHub-side numeric id surfaced as <c>installation_id</c>.</param>
/// <param name="SetupAction">GitHub-supplied <c>setup_action</c>: typically <c>install</c>, sometimes <c>request</c> when admin approval is pending.</param>
/// <param name="Code">OAuth <c>code</c> emitted when the App's "Request user authorization (OAuth) during installation" toggle is on. Present alongside <see cref="InstallationId"/> on User-account installs; absent on Org installs.</param>
public record HandleGithubInstallCallbackCommand(
    string? StateToken,
    string? StateCookieValue,
    long? InstallationId,
    string? SetupAction,
    string? Code = null
) : ICommand<Result<HandleGithubInstallCallbackResponse>>;

public sealed record HandleGithubInstallCallbackResponse
{
    public required string WorkspaceSlug { get; init; }
    public required Guid GithubInstallationId { get; init; }
    public required bool Pending { get; init; }

    /// <summary>
    /// Set when the chosen GitHub account/org is already connected to a
    /// *different* workspace. The install did NOT attach. The controller
    /// redirects the user back to their workspace home with a friendly,
    /// actionable snackbar instead of dumping a raw 400 JSON page mid-redirect.
    /// </summary>
    public bool Conflict { get; init; }

    /// <summary>The GitHub account/org login that's already connected elsewhere (e.g. <c>acme</c>).</summary>
    public string? ConflictAccountLogin { get; init; }

    /// <summary>Display name of the workspace the installation is already attached to.</summary>
    public string? ConflictWorkspaceName { get; init; }
}

public sealed class HandleGithubInstallCallbackHandler
    : ICommandHandler<HandleGithubInstallCallbackCommand, Result<HandleGithubInstallCallbackResponse>>
{
    private readonly ApplicationDbContext _db;
    private readonly IGithubInstallStateService _state;
    private readonly IGithubApiClient _api;
    private readonly IGithubRepositorySyncService _sync;
    private readonly ILogger<HandleGithubInstallCallbackHandler> _logger;

    public HandleGithubInstallCallbackHandler(
        ApplicationDbContext db,
        IGithubInstallStateService state,
        IGithubApiClient api,
        IGithubRepositorySyncService sync,
        ILogger<HandleGithubInstallCallbackHandler> logger)
    {
        _db = db;
        _state = state;
        _api = api;
        _sync = sync;
        _logger = logger;
    }

    public async Task<Result<HandleGithubInstallCallbackResponse>> Handle(
        HandleGithubInstallCallbackCommand request,
        CancellationToken cancellationToken)
    {
        // 1. State validation — both the cookie value and the query param must match the same signed token.
        if (string.IsNullOrEmpty(request.StateToken) || string.IsNullOrEmpty(request.StateCookieValue))
        {
            return Result.Failure<HandleGithubInstallCallbackResponse>("Invalid state");
        }
        if (!string.Equals(request.StateToken, request.StateCookieValue, StringComparison.Ordinal))
        {
            return Result.Failure<HandleGithubInstallCallbackResponse>("Invalid state");
        }

        var workspaceId = _state.Verify(request.StateToken);
        if (workspaceId is null)
        {
            return Result.Failure<HandleGithubInstallCallbackResponse>("Invalid or expired state");
        }

        var workspace = await _db.Workspaces
            .SingleOrDefaultAsync(w => w.Id == workspaceId.Value, cancellationToken);
        if (workspace is null)
        {
            return Result.Failure<HandleGithubInstallCallbackResponse>("Workspace not found");
        }

        if (request.InstallationId is not { } installationId || installationId <= 0)
        {
            return Result.Failure<HandleGithubInstallCallbackResponse>("installation_id is required");
        }

        // 2. Idempotency — same installation hitting the callback twice is fine, just resync.
        //    But an installation already attached to a *different* workspace is a hard error.
        var existing = await _db.GithubInstallations
            .SingleOrDefaultAsync(i => i.InstallationId == installationId, cancellationToken);
        if (existing is not null && existing.WorkspaceId != workspace.Id)
        {
            // A GitHub App installation is one-per-account on GitHub's side, so a
            // second workspace trying to connect the same org round-trips the same
            // installationId and lands here. Don't hard-400 with an opaque
            // "Installation belongs to a different workspace" — name the account
            // and the workspace it's attached to so the user knows exactly what to
            // do (disconnect it there, or pick a different account).
            var otherWorkspace = await _db.Workspaces
                .AsNoTracking()
                .SingleOrDefaultAsync(w => w.Id == existing.WorkspaceId, cancellationToken);
            _logger.LogWarning(
                "GitHub install conflict: installation {InstallationId} ({Account}) already attached to workspace {OtherWorkspaceId}; requested by workspace {WorkspaceId}",
                installationId, existing.AccountLogin, existing.WorkspaceId, workspace.Id);
            return Result.Success(new HandleGithubInstallCallbackResponse
            {
                WorkspaceSlug = workspace.Slug,
                GithubInstallationId = Guid.Empty,
                Pending = false,
                Conflict = true,
                ConflictAccountLogin = existing.AccountLogin,
                ConflictWorkspaceName = otherWorkspace?.Name,
            });
        }

        // 3. setup_action == "request" means the user requested the App but an org admin still
        //    has to approve. We don't have an installation_id we can hit yet — record nothing
        //    and surface a pending state to the caller.
        if (string.Equals(request.SetupAction, "request", StringComparison.OrdinalIgnoreCase) && existing is null)
        {
            _logger.LogInformation(
                "GitHub install callback received with setup_action=request for workspace {WorkspaceId}; awaiting admin approval",
                workspace.Id);
            return Result.Success(new HandleGithubInstallCallbackResponse
            {
                WorkspaceSlug = workspace.Slug,
                GithubInstallationId = Guid.Empty,
                Pending = true,
            });
        }

        GithubInstallation row;
        if (existing is null)
        {
            // 4a. New installation — pull the metadata from GitHub and persist.
            var dto = await _api.GetInstallationAsync(installationId, cancellationToken);
            row = new GithubInstallation
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspace.Id,
                InstallationId = installationId,
                AccountLogin = dto.Account.Login,
                AccountType = dto.Account.Type,
                AccountAvatarUrl = dto.Account.AvatarUrl,
                Suspended = false,
            };
            _db.GithubInstallations.Add(row);
            await _db.SaveChangesAsync(cancellationToken);
        }
        else
        {
            row = existing;
        }

        // 4b. Capture the User Access Token if GitHub sent one. The App has
        //     "Request user authorization (OAuth) during installation" enabled,
        //     so GitHub redirects with both ?installation_id= AND ?code= on
        //     User-account installs (Org installs don't go through the OAuth
        //     leg — `code` will be absent).
        //
        //     Soft-fail policy: if any step of the exchange / /user lookup
        //     fails, we log and continue. The install MUST still succeed; the
        //     user will fall into "Scenario 2" (slim re-authorize banner) the
        //     first time they hit create-new-repo.
        if (!string.IsNullOrWhiteSpace(request.Code))
        {
            await TryCaptureUserAccessTokenAsync(row, request.Code, cancellationToken);
        }

        // 5. Sync repos (works for both first-time and re-callback).
        await _sync.SyncAsync(row.Id, row.InstallationId, cancellationToken);

        return Result.Success(new HandleGithubInstallCallbackResponse
        {
            WorkspaceSlug = workspace.Slug,
            GithubInstallationId = row.Id,
            Pending = false,
        });
    }

    /// <summary>
    /// Exchange an OAuth <c>code</c> for a User Access Token (+ refresh token) and persist
    /// it onto the installation row. Soft-fails on every error path — see the call site for
    /// the rationale (the install itself must always succeed; the user can re-authorize
    /// later via the slim flow if this step bombed).
    /// </summary>
    private async Task TryCaptureUserAccessTokenAsync(
        GithubInstallation row,
        string code,
        CancellationToken ct)
    {
        try
        {
            var payload = await _api.ExchangeOAuthCodeFullAsync(code, ct);

            row.UserAccessToken = payload.AccessToken;
            row.UserAccessTokenExpiresAt = payload.AccessTokenExpiresAt;
            row.UserRefreshToken = payload.RefreshToken;
            row.UserRefreshTokenExpiresAt = payload.RefreshTokenExpiresAt;

            // Best-effort GET /user — the login lets us sanity-check vs AccountLogin
            // (matters for User installs). Failure here is non-fatal — we already have
            // the token captured; the login is a nice-to-have for diagnostics.
            try
            {
                var ghUser = await _api.GetCurrentUserAsync(payload.AccessToken, ct);
                row.UserLogin = ghUser.Login;
                if (string.Equals(row.AccountType, "User", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(row.AccountLogin, ghUser.Login, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "UAT user login {UserLogin} does not match installation account {AccountLogin} (installation {InstallationId})",
                        ghUser.Login, row.AccountLogin, row.InstallationId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "UAT captured but /user lookup failed for installation {InstallationId}",
                    row.InstallationId);
            }

            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Captured GitHub UAT for installation {InstallationId} ({AccountLogin}, type={AccountType})",
                row.InstallationId, row.AccountLogin, row.AccountType);
        }
        catch (Exception ex)
        {
            // Per the spec: log + continue. The install MUST succeed even if UAT capture
            // bombs — the user falls into the slim re-authorize flow on next create-repo.
            _logger.LogWarning(ex,
                "Failed to capture UAT during install callback for installation {InstallationId}; user will need to re-authorize before creating repos",
                row.InstallationId);
        }
    }
}

using Source.Features.GitHub.Services.Dtos;

namespace Source.Features.GitHub.Services;

/// <summary>
/// Thin GitHub REST/HTTP client. Caller-side concerns (caching, persisting rows)
/// live in handlers — this layer only translates calls into HTTP and back.
/// </summary>
public interface IGithubApiClient
{
    /// <summary>App-authenticated <c>GET /app/installations/{id}</c>.</summary>
    Task<GithubInstallationDto> GetInstallationAsync(long installationId, CancellationToken ct = default);

    /// <summary>Installation-authenticated <c>GET /installation/repositories</c>.</summary>
    Task<IReadOnlyList<GithubRepoDto>> ListInstallationRepositoriesAsync(long installationId, CancellationToken ct = default);

    /// <summary>OAuth user-token authenticated <c>GET /user</c>.</summary>
    Task<GithubUserDto> GetCurrentUserAsync(string accessToken, CancellationToken ct = default);

    /// <summary>OAuth user-token authenticated <c>GET /user/emails</c>. Used when <see cref="GithubUserDto.Email"/> is null
    /// because the user keeps their primary email private.</summary>
    Task<IReadOnlyList<GithubEmailDto>> GetCurrentUserEmailsAsync(string accessToken, CancellationToken ct = default);

    /// <summary>Exchanges an OAuth <c>code</c> for a user access token. Returns the access token only.</summary>
    Task<string> ExchangeOAuthCodeAsync(string code, CancellationToken ct = default);

    /// <summary>
    /// Same as <see cref="ExchangeOAuthCodeAsync"/> but returns the full payload — access token + refresh token + expiries —
    /// resolved into absolute UTC timestamps. Used by the install-callback path (and the slim re-authorize flow) to
    /// persist a User Access Token onto a <see cref="Source.Features.GitHub.Models.GithubInstallation"/> row.
    /// </summary>
    Task<GithubUserAccessTokenPayload> ExchangeOAuthCodeFullAsync(string code, CancellationToken ct = default);

    /// <summary>
    /// Exchanges a User Access Token refresh token (<c>ghr_…</c>) for a fresh access token + new refresh token. Used by
    /// <see cref="Source.Features.GitHub.Services.IGithubUserTokenService"/> when the cached UAT is near expiry. Throws on
    /// any non-success status — callers should treat this as "user must re-authorize".
    /// </summary>
    Task<GithubUserAccessTokenPayload> RefreshUserAccessTokenAsync(string refreshToken, CancellationToken ct = default);

    /// <summary>
    /// User-OAuth-authenticated <c>PUT /user/installations/{installationId}/repositories/{repositoryId}</c>.
    /// Adds the given repo to the installation's selected-repos list. The App webhooks and IAT-based reads
    /// don't see freshly-created repos until this is called (for User installs with the common
    /// "Selected repositories" scope). Expects 204 No Content. Soft-fail at the call site is fine — the repo
    /// still exists; the worst case is the user manually adds it via GitHub settings.
    /// </summary>
    Task AddRepoToUserInstallationAsync(string userAccessToken, long installationId, long repositoryId, CancellationToken ct = default);

    /// <summary>
    /// User-OAuth-authenticated <c>POST /repos/{templateOwner}/{templateRepo}/generate</c>. Same shape as
    /// <see cref="CreateRepoFromTemplateAsync"/> but authenticated with a UAT (<c>ghu_…</c>) instead of an IAT.
    /// Required when the target <paramref name="newOwner"/> is a User account — installation tokens cannot create
    /// repos under user namespaces.
    /// </summary>
    Task<GithubRepoDto> CreateRepoFromTemplateWithTokenAsync(
        string accessToken,
        string templateOwner,
        string templateRepo,
        string newOwner,
        string newRepoName,
        string? description,
        bool isPrivate,
        CancellationToken ct = default);

    /// <summary>
    /// User-OAuth-authenticated <c>POST /user/repos</c>. Creates a brand-new empty repo under the authorizing user's
    /// account. UAT-only counterpart to <see cref="CreateInstallationRepositoryAsync"/>'s User branch, which fails
    /// when called with an IAT.
    /// </summary>
    Task<GithubRepoDto> CreateUserRepoWithTokenAsync(
        string accessToken,
        string name,
        string? description,
        bool isPrivate,
        CancellationToken ct = default);

    /// <summary>
    /// Installation-authenticated <c>GET /repos/{owner}/{repo}</c>. Returns the same shape as the
    /// repo objects nested under <c>GET /installation/repositories</c>. Used to read the
    /// <c>default_branch</c> at request time.
    /// </summary>
    Task<GithubRepoDto> GetRepositoryAsync(long installationId, string owner, string repo, CancellationToken ct = default);

    /// <summary>
    /// Installation-authenticated <c>GET /repos/{owner}/{repo}/branches</c>. Paginated; the smoke
    /// test returns up to the first 100 branches — pagination of huge accounts is out of scope.
    /// </summary>
    Task<IReadOnlyList<GithubBranchDto>> ListRepositoryBranchesAsync(long installationId, string owner, string repo, CancellationToken ct = default);

    /// <summary>
    /// Installation-authenticated <c>GET /repos/{owner}/{repo}/git/refs/heads/{branch}</c>.
    /// Returns the tip commit SHA on success; throws <see cref="SourceBranchNotFoundException"/>
    /// when GitHub returns 404 (branch never pushed or deleted from the remote).
    /// </summary>
    Task<string> GetBranchTipShaAsync(string owner, string repo, string branch, long installationId, CancellationToken ct = default);

    /// <summary>
    /// Installation-authenticated <c>POST /repos/{owner}/{repo}/git/refs</c> with body
    /// <c>{ ref: "refs/heads/{newName}", sha }</c>. Throws
    /// <see cref="BranchAlreadyExistsException"/> on 422 (ref already taken) and
    /// <see cref="GitHubBranchCreationForbiddenException"/> on 403 (missing permissions or
    /// branch protection forbids creation).
    /// </summary>
    Task CreateBranchRefAsync(string owner, string repo, string newName, string sha, long installationId, CancellationToken ct = default);

    /// <summary>
    /// Installation-authenticated <c>DELETE /repos/{owner}/{repo}/git/refs/heads/{name}</c>.
    /// Used by the Copy Branch rollback path; 404 responses are swallowed so the call is
    /// idempotent when the ref was already torn down by a previous attempt.
    /// </summary>
    Task DeleteBranchRefAsync(string owner, string repo, string name, long installationId, CancellationToken ct = default);

    /// <summary>
    /// Installation-authenticated <c>GET /repos/{owner}/{repo}/commits/{sha}</c>. Returns
    /// the commit's metadata (author, date, message). Used by the project-scoped branch
    /// picker to surface per-branch "freshness". Returns <c>null</c> on any non-success
    /// status so the caller can collapse the per-branch fields to null without throwing —
    /// a single missing commit must not 500 the whole list endpoint.
    /// </summary>
    Task<GithubCommitDto?> GetCommitAsync(long installationId, string owner, string repo, string sha, CancellationToken ct = default);

    /// <summary>
    /// Installation-authenticated repo creation. Dispatches based on the installation
    /// account type:
    /// <list type="bullet">
    ///   <item>Organization: <c>POST /orgs/{ownerLogin}/repos</c></item>
    ///   <item>User: <c>POST /user/repos</c> (works for installation tokens scoped to a
    ///         user account when the App has Administration:write).</item>
    /// </list>
    /// Uses <c>auto_init: true</c> so the new repo lands with an initial README.md commit
    /// and a default branch (named per <paramref name="defaultBranch"/>) — that's what
    /// the rest of the project-creation flow expects (a ProjectBranch must point at a
    /// real ref). Throws <see cref="GitHubRepoCreateFailedException"/> on 4xx with the
    /// GitHub error message preserved.
    /// </summary>
    Task<GithubRepoDto> CreateInstallationRepositoryAsync(
        long installationId,
        string ownerLogin,
        string accountType,
        string name,
        string? description,
        bool isPrivate,
        string defaultBranch,
        CancellationToken ct = default);

    /// <summary>
    /// Installation-authenticated <c>POST /repos/{templateOwner}/{templateRepo}/generate</c>.
    /// Creates a new repository from a template repo, owned by <paramref name="newOwner"/>.
    /// Body: <c>{ owner, name, description, private, include_all_branches: false }</c>.
    /// Returns the same <see cref="GithubRepoDto"/> shape as
    /// <see cref="CreateInstallationRepositoryAsync"/>. Throws
    /// <see cref="GitHubRepoCreateFailedException"/> on any non-success status (4xx/5xx) with
    /// the GitHub error message preserved — including 404 for missing / private templates.
    /// </summary>
    Task<GithubRepoDto> CreateRepoFromTemplateAsync(
        long installationId,
        string templateOwner,
        string templateRepo,
        string newOwner,
        string newRepoName,
        string? description,
        bool isPrivate,
        CancellationToken ct = default);

    /// <summary>
    /// Installation-authenticated <c>PUT /repos/{owner}/{repo}/contents/{path}</c>. Creates
    /// a file on the given branch with the supplied content (will be base64-encoded by the
    /// implementation). Used to seed additional content beyond the auto-init README.
    /// Throws if the file already exists at that path.
    /// </summary>
    Task CreateFileAsync(
        long installationId,
        string owner,
        string repo,
        string path,
        string content,
        string commitMessage,
        string branch,
        CancellationToken ct = default);
}

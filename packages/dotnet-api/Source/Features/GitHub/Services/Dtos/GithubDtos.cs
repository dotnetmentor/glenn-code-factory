using System.Text.Json.Serialization;

namespace Source.Features.GitHub.Services.Dtos;

/// <summary>Shape of <c>GET /app/installations/{id}</c> (subset we care about).</summary>
public record GithubInstallationDto(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("account")] GithubAccountDto Account);

/// <summary>The <c>account</c> sub-object on an installation.</summary>
public record GithubAccountDto(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("login")] string Login,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("avatar_url")] string? AvatarUrl);

/// <summary>One repo entry in <c>GET /installation/repositories</c>.</summary>
public record GithubRepoDto(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("full_name")] string FullName,
    [property: JsonPropertyName("private")] bool Private,
    [property: JsonPropertyName("default_branch")] string? DefaultBranch,
    [property: JsonPropertyName("owner")] GithubAccountDto Owner);

/// <summary>The <c>repositories</c> wrapper of <c>GET /installation/repositories</c>.</summary>
public record GithubInstallationRepositoriesResponse(
    [property: JsonPropertyName("total_count")] int TotalCount,
    [property: JsonPropertyName("repositories")] List<GithubRepoDto> Repositories);

/// <summary>Shape of <c>GET /user</c> for a signed-in OAuth user.</summary>
public record GithubUserDto(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("login")] string Login,
    [property: JsonPropertyName("avatar_url")] string? AvatarUrl,
    [property: JsonPropertyName("email")] string? Email);

/// <summary>One entry from <c>GET /user/emails</c> — used when the public /user.email is null.</summary>
public record GithubEmailDto(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("primary")] bool Primary,
    [property: JsonPropertyName("verified")] bool Verified,
    [property: JsonPropertyName("visibility")] string? Visibility);

/// <summary>The OAuth code-exchange response from <c>https://github.com/login/oauth/access_token</c>.
/// When the GitHub App has "expiring user tokens" enabled (required for refresh-token rotation),
/// the response also carries <c>expires_in</c>, <c>refresh_token</c>, and <c>refresh_token_expires_in</c>.
/// All three are null on non-expiring tokens (legacy GitHub Apps).</summary>
public record GithubOAuthAccessTokenResponse(
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("token_type")] string? TokenType,
    [property: JsonPropertyName("scope")] string? Scope,
    [property: JsonPropertyName("expires_in")] int? ExpiresIn,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("refresh_token_expires_in")] int? RefreshTokenExpiresIn,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("error_description")] string? ErrorDescription);

/// <summary>
/// Decoded user-access-token payload — the shape callers of
/// <see cref="IGithubApiClient.ExchangeOAuthCodeFullAsync"/> see. Same fields as
/// <see cref="GithubOAuthAccessTokenResponse"/> but with the relative seconds already
/// resolved into absolute UTC expiry timestamps for ease of persistence.
/// </summary>
public sealed record GithubUserAccessTokenPayload
{
    public required string AccessToken { get; init; }
    public DateTime? AccessTokenExpiresAt { get; init; }
    public string? RefreshToken { get; init; }
    public DateTime? RefreshTokenExpiresAt { get; init; }
}

/// <summary>
/// One entry from <c>GET /repos/{owner}/{repo}/branches</c>. The list endpoint embeds a
/// pointer to the tip commit (<c>commit.sha</c>) but not the commit's metadata — those
/// are fetched per-branch via <c>GET /repos/{owner}/{repo}/commits/{sha}</c>.
/// </summary>
public record GithubBranchDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("commit")] GithubBranchTipDto? Commit = null);

/// <summary>
/// The <c>commit</c> sub-document on a <see cref="GithubBranchDto"/>. Only the tip SHA is
/// useful here — author / message / date come from a follow-up commit lookup.
/// </summary>
public record GithubBranchTipDto(
    [property: JsonPropertyName("sha")] string Sha);

/// <summary>
/// Subset of <c>GET /repos/{owner}/{repo}/commits/{sha}</c> we care about for the branch
/// picker's "freshness" column: the committed timestamp, the author's display name (with
/// a fallback to their GitHub login) and the headline of the commit message (first line).
/// </summary>
public record GithubCommitDto(
    [property: JsonPropertyName("sha")] string Sha,
    [property: JsonPropertyName("commit")] GithubCommitDetailDto Commit,
    [property: JsonPropertyName("author")] GithubCommitUserRefDto? Author);

/// <summary>The <c>commit</c> nested object inside <see cref="GithubCommitDto"/>.</summary>
public record GithubCommitDetailDto(
    [property: JsonPropertyName("author")] GithubCommitAuthorDto? Author,
    [property: JsonPropertyName("message")] string? Message);

/// <summary>
/// The <c>commit.author</c> sub-object — the historical author recorded inside the git
/// commit object itself. Carries the human display name and the commit timestamp.
/// </summary>
public record GithubCommitAuthorDto(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("date")] DateTimeOffset? Date);

/// <summary>
/// The top-level <c>author</c> ref on a commit response — points at the GitHub user
/// associated with the author email, when one can be resolved. <c>login</c> is the
/// fall-back display source when the git-side author <c>name</c> is missing.
/// </summary>
public record GithubCommitUserRefDto(
    [property: JsonPropertyName("login")] string? Login);

/// <summary>
/// Shape of <c>GET /repos/{owner}/{repo}/git/refs/heads/{branch}</c> and the response from
/// <c>POST /repos/{owner}/{repo}/git/refs</c>. We only care about the nested <c>object.sha</c>,
/// which is the tip commit of the branch — Copy Branch passes this back into the create-ref
/// call to anchor the new branch at the source's pushed tip.
/// </summary>
public record GithubGitRefDto(
    [property: JsonPropertyName("ref")] string Ref,
    [property: JsonPropertyName("object")] GithubGitRefObjectDto Object);

/// <summary>The <c>object</c> sub-document inside <see cref="GithubGitRefDto"/>.</summary>
public record GithubGitRefObjectDto(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("sha")] string Sha);

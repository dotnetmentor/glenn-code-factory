using Source.Features.GitHub.Models;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.GitHub.Queries.ListInstallationRepos;

/// <summary>
/// Live read of <c>GET /installation/repositories</c> for one of the workspace's GitHub App
/// installations. No caching — the smoke-test repo picker calls this every time the user opens
/// the dropdown.
///
/// <para><see cref="InstallationId"/> is the local DB <see cref="System.Guid"/> primary key of
/// the <see cref="Source.Features.GitHub.Models.GithubInstallation"/> row, NOT the GitHub-side
/// numeric installation id. The handler resolves it to the numeric id internally.</para>
///
/// <para><see cref="CallerUserId"/> is the authenticated user's id (from the JWT). The handler
/// uses it to assert membership of the installation's workspace via <c>WorkspaceMemberships</c>.
/// On a miss — installation does not exist OR caller is not a member — the handler returns a
/// failure prefixed with <see cref="ListInstallationReposHandler.NotFoundPrefix"/> so the
/// controller can map it to 404 (matching card 5's "don't leak existence" rule).</para>
///
/// <para><see cref="WorkspaceId"/> is an OPTIONAL hint: when supplied the handler cross-
/// references each returned repo against the workspace's live projects (matched on the same
/// installation id + repo owner + repo name) and populates
/// <see cref="GithubRepoListItemDto.LinkedProjectId"/> / <c>LinkedProjectName</c> so the
/// frontend can render an "Open existing project" affordance without a second round-trip. When
/// omitted (existing callers) both fields are left <c>null</c> — additive, fully backward-
/// compatible. The handler still requires the caller to be a member of the installation's own
/// workspace; the optional workspace param does not relax that check.</para>
/// </summary>
public sealed record ListInstallationReposQuery(Guid InstallationId, string CallerUserId, Guid? WorkspaceId = null)
    : IQuery<Result<List<GithubRepoListItemDto>>>;

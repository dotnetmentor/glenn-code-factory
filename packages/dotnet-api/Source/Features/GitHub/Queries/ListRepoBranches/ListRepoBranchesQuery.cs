using Source.Features.GitHub.Models;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.GitHub.Queries.ListRepoBranches;

/// <summary>
/// Live read of <c>GET /repos/{owner}/{repo}/branches</c> for a repo accessible through the
/// installation. No caching. The handler also calls <c>GET /repos/{owner}/{repo}</c> once to
/// discover the default branch so it can flag the matching entry as
/// <see cref="GithubBranchListItemDto.IsDefault"/>.
///
/// <para>Same 404-on-miss semantics as <c>ListInstallationReposQuery</c> — see that query's
/// summary for the rationale.</para>
/// </summary>
public sealed record ListRepoBranchesQuery(
    Guid InstallationId,
    string Owner,
    string Repo,
    string CallerUserId) : IQuery<Result<List<GithubBranchListItemDto>>>;

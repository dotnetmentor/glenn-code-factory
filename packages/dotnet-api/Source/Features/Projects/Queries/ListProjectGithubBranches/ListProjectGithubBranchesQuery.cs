using Source.Features.GitHub.Models;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Queries.ListProjectGithubBranches;

/// <summary>
/// Project-scoped variant of <see cref="GitHub.Queries.ListRepoBranches.ListRepoBranchesQuery"/>.
/// Returns the live list of git branches on the project's GitHub repo, enriched with the
/// id of the matching <see cref="Projects.Models.ProjectBranch"/> (1:1 by branch name) when
/// one exists.
///
/// <para>Powers the "New Session" branch picker — the frontend renders an "Open" action
/// for git branches that already have a system branch and an "Attach / Fork" action for
/// those that do not.</para>
///
/// <para>Same 404-on-miss semantics as the installation-scoped variant: a missing project,
/// soft-deleted project or non-member caller all collapse to a single "not found" so
/// existence cannot be probed.</para>
/// </summary>
public sealed record ListProjectGithubBranchesQuery(
    Guid ProjectId,
    string CallerUserId
) : IQuery<Result<List<GithubBranchListItemDto>>>;

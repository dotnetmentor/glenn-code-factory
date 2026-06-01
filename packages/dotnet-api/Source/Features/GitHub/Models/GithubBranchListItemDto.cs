namespace Source.Features.GitHub.Models;

/// <summary>
/// Controller-shape DTO for the live branch picker
/// (<c>GET /api/github/installations/{id}/repos/{owner}/{repo}/branches</c>) and the
/// project-scoped variant (<c>GET /api/projects/{projectId}/github-branches</c>).
/// <see cref="IsDefault"/> is computed by comparing the branch name against the repo's
/// <c>default_branch</c> field at request time.
///
/// <para><see cref="LinkedSystemBranchId"/> is populated ONLY by the project-scoped
/// endpoint and surfaces the <see cref="Source.Features.Projects.Models.ProjectBranch"/>
/// id that is already linked to this git branch (1:1 by branch name). <c>null</c>
/// means "this git branch has no system branch yet" — the frontend renders an
/// "Attach / Fork" action in that case, and "Open" otherwise. Always <c>null</c>
/// for callers of the installation-scoped endpoint because that endpoint is
/// repo-only and has no project context.</para>
///
/// <para><see cref="LastCommitAt"/>, <see cref="LastCommitAuthor"/> and
/// <see cref="LastCommitMessage"/> are populated by the project-scoped endpoint
/// for the branch picker UX ("freshness" indicator next to each branch). The
/// installation-scoped variant leaves them <c>null</c> — that picker doesn't
/// surface freshness today, so we avoid the extra GitHub round-trips. Each
/// field is individually nullable: a missing or unreadable commit response for
/// one branch must NOT 500 the whole list, so per-branch failures collapse the
/// fields to <c>null</c> for that row only.</para>
/// </summary>
public sealed record GithubBranchListItemDto(
    string Name,
    bool IsDefault,
    Guid? LinkedSystemBranchId = null,
    DateTimeOffset? LastCommitAt = null,
    string? LastCommitAuthor = null,
    string? LastCommitMessage = null);

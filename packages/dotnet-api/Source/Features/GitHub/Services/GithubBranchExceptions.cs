namespace Source.Features.GitHub.Services;

/// <summary>
/// Thrown by <see cref="IGithubApiClient.GetBranchTipShaAsync"/> when GitHub returns 404
/// for the requested branch ref — i.e. the source branch has never been pushed (or was
/// deleted from the remote). The Copy Branch orchestrator translates this into the
/// user-facing "push your source branch first" error before any Fly resources are touched.
/// </summary>
public class SourceBranchNotFoundException : Exception
{
    /// <summary>Repository owner (login) the lookup was scoped to.</summary>
    public string Owner { get; }

    /// <summary>Repository name the lookup was scoped to.</summary>
    public string Repo { get; }

    /// <summary>Branch name that did not exist on the remote.</summary>
    public string Branch { get; }

    public SourceBranchNotFoundException(string owner, string repo, string branch)
        : base($"Branch '{branch}' does not exist on {owner}/{repo} (GitHub returned 404).")
    {
        Owner = owner;
        Repo = repo;
        Branch = branch;
    }
}

/// <summary>
/// Thrown by <see cref="IGithubApiClient.CreateBranchRefAsync"/> when GitHub returns 422 —
/// the ref name is already taken on the remote. The Copy Branch orchestrator surfaces this
/// as an inline naming-collision error in the dialog (the same name guard the auto-suffix
/// rule tries to avoid client-side, kept as a backstop against races).
/// </summary>
public class BranchAlreadyExistsException : Exception
{
    /// <summary>Repository owner (login) the create was scoped to.</summary>
    public string Owner { get; }

    /// <summary>Repository name the create was scoped to.</summary>
    public string Repo { get; }

    /// <summary>Branch name that already existed on the remote.</summary>
    public string Branch { get; }

    public BranchAlreadyExistsException(string owner, string repo, string branch)
        : base($"Branch '{branch}' already exists on {owner}/{repo} (GitHub returned 422).")
    {
        Owner = owner;
        Repo = repo;
        Branch = branch;
    }
}

/// <summary>
/// Thrown by <see cref="IGithubApiClient.CreateBranchRefAsync"/> when GitHub returns 403 —
/// the installation lacks <c>contents:write</c> or branch protection forbids ref creation
/// by the app. Surfaced verbatim by the orchestrator so the user can fix the install
/// scope or the protection rule.
/// </summary>
public class GitHubBranchCreationForbiddenException : Exception
{
    /// <summary>Repository owner (login) the create was scoped to.</summary>
    public string Owner { get; }

    /// <summary>Repository name the create was scoped to.</summary>
    public string Repo { get; }

    /// <summary>Branch name that was refused.</summary>
    public string Branch { get; }

    public GitHubBranchCreationForbiddenException(string owner, string repo, string branch, string? detail = null)
        : base($"GitHub forbade creating branch '{branch}' on {owner}/{repo} (403)."
               + (string.IsNullOrWhiteSpace(detail) ? string.Empty : $" Detail: {detail}"))
    {
        Owner = owner;
        Repo = repo;
        Branch = branch;
    }
}

/// <summary>
/// Thrown by <see cref="IGithubApiClient.CreateInstallationRepositoryAsync"/> when GitHub
/// refuses to create the repo (4xx response). Carries the HTTP status code and the
/// message extracted from GitHub's JSON body so handlers can surface actionable detail
/// (e.g. "name already exists on this account", "Administration:write missing").
/// </summary>
public class GitHubRepoCreateFailedException : Exception
{
    public int StatusCode { get; }
    public string OwnerLogin { get; }
    public string RepoName { get; }

    public GitHubRepoCreateFailedException(int statusCode, string ownerLogin, string repoName, string? detail)
        : base($"GitHub refused to create repository '{ownerLogin}/{repoName}' ({statusCode})."
               + (string.IsNullOrWhiteSpace(detail) ? string.Empty : $" Detail: {detail}"))
    {
        StatusCode = statusCode;
        OwnerLogin = ownerLogin;
        RepoName = repoName;
    }
}

using Source.Shared;
using Source.Shared.Events;

namespace Source.Features.GitHub.Models;

/// <summary>
/// A single repository connected through a <see cref="GithubInstallation"/>.
/// Maintained by both the install callback (initial sync) and the
/// <c>installation_repositories</c> webhook (subsequent add/remove).
/// </summary>
public class GithubRepository : Entity, IAuditable
{
    public Guid Id { get; set; }

    /// <summary>FK to the parent installation.</summary>
    public Guid GithubInstallationId { get; set; }
    public GithubInstallation? Installation { get; set; }

    /// <summary>The GitHub-side numeric repository id.</summary>
    public long GithubRepoId { get; set; }

    /// <summary>Owner login (user or org). Denormalised for filter speed.</summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>Repository name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>"owner/name" — denormalised for display + lookup.</summary>
    public string FullName { get; set; } = string.Empty;

    /// <summary>True if the repo is private (purely informational).</summary>
    public bool Private { get; set; }

    /// <summary>The default branch name (e.g. "main"). Nullable until first sync resolves it.</summary>
    public string? DefaultBranch { get; set; }

    /// <summary>UTC timestamp of the last successful sync from the GitHub API.</summary>
    public DateTime? LastSyncedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

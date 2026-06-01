using Source.Features.GitHub.Services.Dtos;

namespace Source.Features.GitHub.Services;

/// <summary>
/// Encapsulates the repo upsert/prune dance shared by:
///   - the manual sync command (POST /github/repositories/sync)
///   - the install-callback flow (after the installation row is persisted).
///   - the <c>installation_repositories</c> webhook (added/removed deltas).
/// Always batches changes into a single SaveChanges to keep the operation atomic.
/// </summary>
public interface IGithubRepositorySyncService
{
    /// <summary>
    /// Reconcile the persisted <c>GithubRepository</c> rows for an installation against
    /// the live list returned by GitHub. Adds new, updates changed, hard-removes any that
    /// are no longer present.
    /// </summary>
    Task<RepositorySyncResult> SyncAsync(Guid githubInstallationId, long githubInstallationNumericId, CancellationToken ct = default);

    /// <summary>
    /// Insert-or-update the supplied repos for an installation. Used by the
    /// <c>installation_repositories.added</c> webhook where GitHub gives us a delta
    /// rather than a snapshot. Does NOT delete anything — see <see cref="RemoveByGithubRepoIdsAsync"/>
    /// for the inverse.
    /// </summary>
    Task UpsertFromWebhookAsync(Guid githubInstallationId, IEnumerable<GithubWebhookRepoDto> repos, CancellationToken ct = default);

    /// <summary>
    /// Hard-delete repos by their GitHub-side numeric id within a single installation.
    /// Used by the <c>installation_repositories.removed</c> webhook.
    /// Missing rows are silently skipped (idempotent).
    /// </summary>
    Task RemoveByGithubRepoIdsAsync(Guid githubInstallationId, IEnumerable<long> githubRepoIds, CancellationToken ct = default);
}

/// <summary>Counts surfaced from a sync run — drives the JSON response of the manual endpoint.</summary>
public sealed record RepositorySyncResult(int Added, int Updated, int Removed, int Total);

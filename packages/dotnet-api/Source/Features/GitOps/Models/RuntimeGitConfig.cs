using Source.Shared;
using Source.Shared.Events;

namespace Source.Features.GitOps.Models;

/// <summary>
/// Per-runtime git configuration. One row per
/// <see cref="RuntimeLifecycle.Models.ProjectRuntime"/> (enforced by a unique
/// index on <see cref="RuntimeId"/>) carrying the daemon's
/// <c>autoCommit</c> toggle and optional SSH deploy key for git push auth.
///
/// <list type="bullet">
///   <item><see cref="AutoCommit"/> defaults to <c>true</c>: a freshly
///         provisioned runtime auto-commits in the daemon by default. Operators
///         flip this off via the admin endpoint when a project needs hand-curated
///         commits (mirrors the <c>RuntimeHookConfig</c> default-on shape).</item>
///   <item><see cref="DeployKey"/> is the raw OpenSSH/RSA/ED25519 private key
///         the daemon uses for <c>git push</c> auth. Stored plaintext for v1 —
///         the postgres data store is already in the trust boundary, and the
///         only consumers are the daemon (over TLS via SignalR) and SuperAdmin
///         operators. <b>TODO</b>: encrypt at rest under a workspace-scoped key
///         when the secrets feature lands. Tracked under daemon-git-ops backlog.</item>
///   <item><see cref="DeployKeyHostKey"/> is the matching <c>known_hosts</c>
///         line — typically <c>github.com ssh-ed25519 AAAA...</c> — the daemon
///         writes alongside the key so first-push doesn't prompt for host
///         verification. Optional; null falls back to the daemon's bundled
///         baseline (GitHub/GitLab/Bitbucket pinned defaults).</item>
///   <item>FK to <c>ProjectRuntime</c> on <see cref="RuntimeId"/> with
///         <c>NoAction</c>: same outlive-the-row reasoning as
///         <see cref="Hooks.Models.RuntimeHookConfig"/> — runtimes are
///         soft-deleted, a stale config row is harmless (no daemon to push to),
///         the 30-day janitor doubles as cleanup.</item>
///   <item>Soft-deletable so operators can hide a row without losing the audit
///         trail (and so the global query filter applies).</item>
///   <item>Admin path is the only writer. The bootstrap delivery in
///         <c>RuntimeHub.OnConnectedAsync</c> reads <see cref="AutoCommit"/> +
///         <see cref="DeployKey"/> and pushes them to the connecting daemon as
///         a one-shot <c>UpdateConfig</c>; subsequent edits route through the
///         admin endpoints, which both persist here and push the same way.</item>
/// </list>
///
/// <para>Inherits <see cref="Entity"/> so future cards can raise events from
/// instance methods without a model change — even though this card has none.</para>
/// </summary>
public class RuntimeGitConfig : Entity, IAuditable, ISoftDelete
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Runtime this config applies to. Unique index — one config per runtime.
    /// FK to <c>ProjectRuntime</c> with <c>NoAction</c> for the same
    /// outlive-the-row reasoning as
    /// <see cref="Hooks.Models.RuntimeHookConfig.RuntimeId"/>.
    /// </summary>
    public Guid RuntimeId { get; set; }

    /// <summary>
    /// Whether the daemon auto-commits after each successful turn. Defaults to
    /// <c>true</c> — most projects want the auto-commit cadence; operators opt
    /// out per-runtime via the admin endpoint.
    /// </summary>
    public bool AutoCommit { get; set; } = true;

    /// <summary>
    /// SSH private key (OpenSSH/RSA/DSA/EC/ED25519) for git push auth.
    /// Plaintext at rest for v1.
    /// <b>TODO</b>: encrypt under a workspace-scoped key once the secrets
    /// feature lands (daemon-git-ops backlog).
    /// </summary>
    public string? DeployKey { get; set; }

    /// <summary>
    /// Matching <c>known_hosts</c> line for the deploy key's host. Optional —
    /// when null the daemon falls back to its bundled defaults for GitHub /
    /// GitLab / Bitbucket.
    /// </summary>
    public string? DeployKeyHostKey { get; set; }

    // -------- IAuditable --------
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // -------- ISoftDelete --------
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
}

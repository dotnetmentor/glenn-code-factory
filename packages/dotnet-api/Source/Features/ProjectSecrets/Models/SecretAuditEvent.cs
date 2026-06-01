namespace Source.Features.ProjectSecrets.Models;

/// <summary>
/// Append-only audit row capturing a single action against the project-secrets
/// subsystem — write, read, list, denial, or daemon delivery.
///
/// <list type="bullet">
///   <item>Immutable. <b>NOT IAuditable</b>, <b>NOT ISoftDelete</b> — the row has
///         only <see cref="CreatedAt"/> as a domain timestamp; we never want a
///         global interceptor stamping audit rows. Mirrors the
///         <c>RuntimeTokenIssue</c> / <c>RuntimeStateEvent</c> pattern.</item>
///   <item>No FK to <c>ProjectSecret</c> on <see cref="SecretId"/> — the audit
///         trail must outlive a hard-delete of the secret row, mirroring the
///         <c>FlyOperation.RuntimeId</c> / <c>BootstrapRun.RuntimeId</c>
///         convention. Same reasoning for <see cref="ProjectId"/>.</item>
///   <item><see cref="SecretKey"/> records only the env-var name —
///         <b>never the value</b>. Capped at 200 chars to mirror
///         <c>ProjectSecret.Key</c>; nullable because some actions (e.g.
///         <see cref="SecretAuditAction.ListAttempted"/>,
///         <see cref="SecretAuditAction.CrossTenantDenied"/>) don't pin a single
///         key.</item>
///   <item><see cref="Actor"/> is the principal that performed the action —
///         typically <c>User.Id</c> (string up to 450 chars to fit Identity's
///         user ids), but may also be <c>"daemon"</c> /
///         <c>"system:bootstrap"</c> for automated paths. Free-form on purpose.</item>
///   <item><see cref="Metadata"/> is opaque jsonb for action-specific context
///         (e.g. revealed-by IP, list count, denial reason). The shape lives in
///         code, not the schema, so additions don't require a migration.</item>
///   <item>Composite index on (ProjectId, CreatedAt DESC) — the dominant read
///         pattern is "audit trail for project X, latest first". DESC on
///         <see cref="CreatedAt"/> is applied via raw SQL in the migration since
///         EF Core 9 doesn't expose per-column sort order on relational indexes
///         (matches the <c>FlyOperation</c> / <c>BootstrapRun</c> /
///         <c>RuntimeStateEvent</c> precedent).</item>
/// </list>
/// </summary>
public class SecretAuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>What happened. See <see cref="SecretAuditAction"/> for semantics.</summary>
    public SecretAuditAction Action { get; set; }

    /// <summary>
    /// Project the action targeted. Plain Guid (no FK) — the audit row must
    /// outlive any future project hard-delete.
    /// </summary>
    public Guid ProjectId { get; set; }

    /// <summary>
    /// The specific secret the action targeted, when applicable. Plain Guid (no
    /// FK) — null for actions that don't pin a single secret
    /// (<see cref="SecretAuditAction.ListAttempted"/>,
    /// <see cref="SecretAuditAction.CrossTenantDenied"/>) and outlives a
    /// hard-delete of the underlying <see cref="ProjectSecret"/> row.
    /// </summary>
    public Guid? SecretId { get; set; }

    /// <summary>
    /// Env-var name only — <b>never the value</b>. Capped at 200 chars to mirror
    /// <see cref="ProjectSecret.Key"/>. Null for actions that don't pin a key.
    /// </summary>
    public string? SecretKey { get; set; }

    /// <summary>
    /// Principal that performed the action. Typically <c>User.Id</c> (Identity
    /// user ids are strings up to 450 chars), but may also be a system-token
    /// label like <c>"daemon"</c> for automated paths. Free-form.
    /// </summary>
    public string Actor { get; set; } = string.Empty;

    /// <summary>
    /// Opaque action-specific context (jsonb). Shape lives in code so additions
    /// don't require a migration.
    /// </summary>
    public string? Metadata { get; set; }

    /// <summary>UTC timestamp when the action was logged. Set by the audit writer; never an interceptor.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

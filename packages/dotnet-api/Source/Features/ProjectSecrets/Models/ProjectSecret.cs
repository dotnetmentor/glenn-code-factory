using Source.Features.Projects.Models;
using Source.Shared;
using Source.Shared.Events;

namespace Source.Features.ProjectSecrets.Models;

/// <summary>
/// One encrypted environment-variable secret scoped to a project. The plaintext
/// value never lands in this table — only its ciphertext + the AEAD nonce, plus
/// the version of the data encryption key (DEK) that was used to wrap it.
///
/// <list type="bullet">
///   <item><see cref="ProjectId"/> is a plain Guid (no FK). The Project entity is
///         owned by another feature slice; this mirrors the
///         <c>ProjectRuntime.ProjectId</c> / <c>HookExecution.RuntimeId</c>
///         convention — the secret row outlives any future project hard-delete.</item>
///   <item><see cref="Key"/> is the env-var name (e.g. <c>STRIPE_API_KEY</c>),
///         capped at 200 chars. A unique partial index on (ProjectId, Key) where
///         <c>DeletedAt IS NULL</c> enforces "one live row per (project, key)";
///         deleted rows are kept for audit and ignored by the constraint so the
///         operator can re-add a deleted key without conflict.</item>
///   <item><see cref="Ciphertext"/> + <see cref="Nonce"/> hold the AES-GCM (or
///         equivalent AEAD) output produced by the encryption service in Card 2.
///         <see cref="DekVersion"/> records which wrapped DEK was used so the
///         service can decrypt across DEK rotations.</item>
///   <item><see cref="Version"/> is bumped on each update so the daemon's
///         bootstrap handshake (Card 4) can detect changes without comparing
///         ciphertext bytes; defaults to 1 on insert.</item>
///   <item><see cref="CreatedBy"/> is the FK to <c>User.Id</c> recorded for
///         "who first wrote this secret". Optional reference — Identity user ids
///         are strings up to 450 chars; nullable for system-seeded rows.</item>
///   <item>Soft-deletable so the audit trail and downstream foreign references
///         from <c>SecretAuditEvent.SecretId</c> remain valid; the global
///         query filter hides deleted rows from default queries.</item>
/// </list>
///
/// <para>Inherits <see cref="Entity"/> so future cards can raise events from
/// instance methods without a model change — even though this card doesn't.</para>
/// </summary>
public class ProjectSecret : Entity, IAuditable, ISoftDelete
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Project the secret belongs to. Plain Guid (no FK) — the Project entity is
    /// owned by another slice and the secret row must outlive a project hard-delete.
    /// Same outlive-the-row reasoning as <c>ProjectRuntime.ProjectId</c>.
    /// </summary>
    public Guid ProjectId { get; set; }

    /// <summary>
    /// Branch this secret is scoped to. <c>null</c> means "project-wide default" —
    /// the value applies to every branch unless a branch-specific row overrides
    /// it. A non-null value pins the secret to one <see cref="ProjectBranch"/>;
    /// the branch-effective resolution (bootstrap + hot-apply) overlays
    /// project-wide rows with branch-specific ones, branch winning per key.
    ///
    /// <para>FK to <c>ProjectBranch</c> with <c>OnDelete=Restrict</c> — matches
    /// the <c>ProjectRuntime.BranchId</c> / <c>Conversation.BranchId</c>
    /// convention: branches are never deleted in v1, and Restrict surfaces any
    /// future "delete a branch with scoped secrets" attempt rather than silently
    /// orphaning (or cascade-nuking) the env vars.</para>
    /// </summary>
    public Guid? BranchId { get; set; }

    /// <summary>
    /// Environment variable name (e.g. <c>STRIPE_API_KEY</c>). Capped at 200 chars.
    /// Part of the unique partial index on (ProjectId, BranchId, Key) where the
    /// row isn't soft-deleted.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Whether this row holds a true secret (masked, reveal-only) vs a plain
    /// config value that may be shown in list responses. Defaults to <c>true</c>
    /// so existing rows and the conservative default are "treat as secret".
    /// Masking/reveal behaviour is enforced by the read paths, not here.
    /// </summary>
    public bool IsSecret { get; set; } = true;

    /// <summary>
    /// AEAD ciphertext of the secret value. Opaque bytes — only the encryption
    /// service in Card 2 interprets them. Never logged.
    /// </summary>
    public byte[] Ciphertext { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// AEAD nonce / IV used when producing <see cref="Ciphertext"/>. Stored
    /// alongside the ciphertext so decryption is self-contained.
    /// </summary>
    public byte[] Nonce { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Which wrapped DEK version produced this ciphertext. Defaults to 1; bumped
    /// when the project's DEK is rotated and the row is re-encrypted.
    /// </summary>
    public int DekVersion { get; set; } = 1;

    /// <summary>
    /// Monotonic version number for this (project, key) pair. Starts at 1, bumps
    /// on every update so the daemon bootstrap handshake (Card 4) can detect
    /// changes cheaply without comparing ciphertext bytes.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// FK to <c>User.Id</c> — the principal that first wrote this secret.
    /// Optional reference; null for system-seeded rows. Identity user ids are
    /// strings up to 450 chars.
    /// </summary>
    public string? CreatedBy { get; set; }

    // -------- IAuditable --------
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // -------- ISoftDelete --------
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
}

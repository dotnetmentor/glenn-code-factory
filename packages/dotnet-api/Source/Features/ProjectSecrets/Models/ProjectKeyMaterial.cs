using Source.Shared;
using Source.Shared.Events;

namespace Source.Features.ProjectSecrets.Models;

/// <summary>
/// Per-project envelope-encryption key material. One row per project (enforced
/// by a unique index on <see cref="ProjectId"/>) carrying the project's
/// <see cref="WrappedDek"/> — the data encryption key wrapped under the master
/// key version recorded in <see cref="MasterKeyVersion"/>.
///
/// <list type="bullet">
///   <item>Lazily created on first secret write (Card 2 handles that). A project
///         with no secrets has no row here.</item>
///   <item><see cref="WrappedDek"/> is opaque bytes — the AEAD output of wrapping
///         the per-project DEK with the active master key. The encryption service
///         in Card 2 unwraps it on demand and never persists the plaintext DEK.</item>
///   <item><see cref="MasterKeyVersion"/> records which master key wrapped this
///         row's DEK so the service can rewrap during master-key rotation without
///         re-encrypting any of the underlying <see cref="ProjectSecret"/> rows.</item>
///   <item><see cref="ProjectId"/> is a plain Guid (no FK) — the Project entity is
///         owned by another slice and this row must outlive a project hard-delete
///         long enough for the audit trail to stay coherent. Mirrors
///         <c>ProjectSecret.ProjectId</c>.</item>
///   <item>Soft-deletable so a project's key material can be retired without
///         breaking historical references; the global query filter hides deleted
///         rows from default queries.</item>
/// </list>
///
/// <para>Inherits <see cref="Entity"/> so future cards can raise events from
/// instance methods without a model change.</para>
/// </summary>
public class ProjectKeyMaterial : Entity, IAuditable, ISoftDelete
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Project this key material belongs to. Unique index — one row per project.
    /// Plain Guid (no FK) — same outlive-the-row reasoning as
    /// <c>ProjectSecret.ProjectId</c>.
    /// </summary>
    public Guid ProjectId { get; set; }

    /// <summary>
    /// AEAD-wrapped data encryption key (DEK) for this project. Opaque bytes —
    /// only the encryption service in Card 2 unwraps it. Never logged.
    /// </summary>
    public byte[] WrappedDek { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Version of the master key that wrapped <see cref="WrappedDek"/>. Defaults
    /// to 1; bumped during master-key rotation, at which point the service rewraps
    /// the DEK under the new master without touching any <c>ProjectSecret</c> rows.
    /// </summary>
    public int MasterKeyVersion { get; set; } = 1;

    // -------- IAuditable --------
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // -------- ISoftDelete --------
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
}

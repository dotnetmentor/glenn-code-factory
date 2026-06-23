using Source.Shared;
using Source.Shared.Events;

namespace Source.Features.ClaudeModels.Models;

/// <summary>
/// Catalog row for a single Claude Agent SDK model the platform exposes through
/// the chat surface when a conversation's <c>AgentBackend</c> is <c>"claude"</c>.
/// Sibling to <see cref="Source.Features.CursorModels.Models.CursorModel"/> in
/// shape and ownership (super-admin curated, no CI registration, soft-deletable
/// and auditable so historical session FKs survive a tombstone), but the
/// catalog metadata is the Claude flavour: reasoning capability + default
/// effort instead of the Cursor SDK's variant / parameter matrix.
///
/// <list type="bullet">
///   <item><see cref="Slug"/> is the Claude model id the daemon forwards to
///         <c>@anthropic-ai/claude-agent-sdk</c> (e.g. <c>"claude-opus-4-8"</c>).
///         Unique among non-tombstoned rows.</item>
///   <item><see cref="DisplayName"/> is the human-readable label the picker
///         renders (e.g. <c>"Claude Opus 4.8"</c>).</item>
///   <item><see cref="IsSystemDefault"/> marks the single model the daemon
///         falls back to when a conversation has no explicit Claude model.
///         Enforced unique at the DB level by a filtered index (mirrors the
///         old <c>AgentModels.OnlyOneSystemDefault</c> index).</item>
///   <item><see cref="SupportsReasoning"/> gates the reasoning-effort dropdown
///         in the composer — only reasoning-capable models surface the
///         Low / Medium / High / Max selector.</item>
///   <item><see cref="DefaultEffort"/> is the per-model default reasoning
///         effort (<c>low</c>|<c>medium</c>|<c>high</c>|<c>xhigh</c>|<c>max</c>),
///         used when the conversation has no explicit override. Null for
///         non-reasoning models.</item>
///   <item><see cref="IsActive"/> hides retired models from the picker without
///         deleting them — historical session rows keep their FK so the audit
///         trail survives.</item>
///   <item>Soft-deletable + auditable. FKs from Projects / AgentSessions use
///         ON DELETE SET NULL so the catalog can shrink without breaking
///         outstanding references.</item>
/// </list>
///
/// <para>POCO for this slice. No state-transition methods or domain events —
/// the slice only seeds rows and exposes a read endpoint; the
/// StoredEntityChanges interceptor still captures column-level history for
/// compliance if rows are mutated later.</para>
/// </summary>
public class ClaudeModel : Entity, IAuditable, ISoftDelete
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Claude model id, e.g. <c>"claude-opus-4-8"</c>. Required, max 100 chars,
    /// unique among non-tombstoned rows. The daemon's <c>ClaudeFactory</c>
    /// forwards this verbatim to the SDK <c>query()</c> <c>model</c> option.
    /// </summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable display name shown in the model picker. Required, max 200
    /// chars. Free-form — operators can rename without touching <see cref="Slug"/>.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Optional longer-form description shown alongside the picker entry.
    /// Free-form, max 500 chars, nullable.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// The single platform default Claude model. Exactly one non-tombstoned row
    /// carries <c>true</c>, guarded by a filtered-unique index. The daemon falls
    /// back to this when a conversation selects the Claude backend but no
    /// explicit model.
    /// </summary>
    public bool IsSystemDefault { get; set; }

    /// <summary>
    /// True when the model supports adaptive thinking / reasoning effort. Gates
    /// the reasoning-effort dropdown in the composer.
    /// </summary>
    public bool SupportsReasoning { get; set; }

    /// <summary>
    /// Per-model default reasoning effort: <c>low</c> | <c>medium</c> |
    /// <c>high</c> | <c>xhigh</c> | <c>max</c>. Null for non-reasoning models.
    /// Used as the effort when the conversation has no explicit override.
    /// </summary>
    public string? DefaultEffort { get; set; }

    /// <summary>
    /// When <c>true</c> the model appears in the picker; when <c>false</c> it's
    /// hidden from end users but keeps its row so historical FKs survive. Use
    /// soft-delete (<see cref="ISoftDelete.IsDeleted"/>) for "wipe from
    /// management surface entirely" semantics.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Stable display order for the picker. Lower comes first.
    /// </summary>
    public int SortOrder { get; set; }

    // -------- IAuditable --------
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // -------- ISoftDelete --------
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
}

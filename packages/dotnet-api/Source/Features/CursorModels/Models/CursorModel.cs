using Source.Shared;
using Source.Shared.Events;

namespace Source.Features.CursorModels.Models;

/// <summary>
/// Catalog row for a single Cursor SDK model the platform exposes through the
/// chat surface when a project's <c>AgentBackend</c> is <c>"cursor"</c>.
/// Mirrors <see cref="Source.Features.OpencodeModels.Models.OpencodeModel"/>
/// exactly in shape and ownership: super-admin curated, no CI registration,
/// soft-deletable and auditable so historical session FKs survive a tombstone.
///
/// <list type="bullet">
///   <item><see cref="Slug"/> is the Cursor SDK model id the daemon forwards
///         to <c>@cursor/sdk</c> (e.g. <c>"composer-2"</c>). Unique among
///         non-tombstoned rows.</item>
///   <item><see cref="DisplayName"/> is the human-readable label the picker
///         renders (e.g. <c>"Composer 2"</c>).</item>
///   <item><see cref="IsActive"/> hides retired models from the picker without
///         deleting them — historical project / session rows keep their FK so
///         the audit trail survives.</item>
///   <item><see cref="Aliases"/> / <see cref="Parameters"/> / <see cref="Variants"/>
///         mirror the <c>@cursor/sdk</c> <c>Cursor.models.list()</c> payload
///         verbatim so the frontend can render variant pickers / parameter
///         dropdowns without a second round-trip. Stored as <c>jsonb</c> and
///         round-tripped natively via Npgsql's dynamic JSON serializer
///         (<c>EnableDynamicJson()</c> in <c>DatabaseExtensions</c>).</item>
///   <item>Soft-deletable + auditable, mirroring <c>OpencodeModel</c>. FKs
///         from Projects / AgentSessions use ON DELETE SET NULL so the
///         catalog can shrink without breaking outstanding references.</item>
/// </list>
///
/// <para>POCO for this slice. No state-transition methods or domain events —
/// the slice only seeds rows and exposes a read endpoint; the
/// StoredEntityChanges interceptor still captures column-level history for
/// compliance if rows are mutated later.</para>
/// </summary>
public class CursorModel : Entity, IAuditable, ISoftDelete
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Cursor SDK model id, e.g. <c>"composer-2"</c>. Required, max 100 chars,
    /// unique among active rows. Lowercase alphanumeric with hyphens and dots
    /// (slugs in this catalogue contain version numbers like <c>"5.5"</c>, so
    /// we don't constrain to <c>[a-z0-9-]+</c> at the entity level — slug
    /// validation lives in the command layer when admin CRUD is added in a
    /// later slice).
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
    /// When <c>true</c> the model appears in the picker; when <c>false</c> it's
    /// hidden from end users but keeps its row so historical FKs survive. Use
    /// soft-delete (<see cref="ISoftDelete.IsDeleted"/>) for "wipe from
    /// management surface entirely" semantics.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Alternative slugs the Cursor SDK accepts for this model
    /// (e.g. <c>composer-2.5</c> also responds to <c>composer-latest</c>,
    /// <c>composer</c>, <c>composer-2-5</c>). Stored as <c>jsonb</c>. Defaults
    /// to an empty list when the SDK didn't expose any aliases.
    /// </summary>
    public List<string> Aliases { get; set; } = [];

    /// <summary>
    /// Tunable parameter definitions for this model — each entry pairs a
    /// parameter id (e.g. <c>reasoning</c>, <c>context</c>, <c>fast</c>) with
    /// the discrete set of values the SDK accepts. The frontend renders these
    /// as dropdowns / toggles in the model picker. Stored as <c>jsonb</c>.
    /// Defaults to an empty list for models with no tunable parameters.
    /// </summary>
    public List<CursorModelParameter> Parameters { get; set; } = [];

    /// <summary>
    /// Concrete parameter combinations the SDK pre-resolves for this model.
    /// Always at least one entry — even parameter-less models have a single
    /// <c>params: []</c> variant marked <c>IsDefault</c>. Stored as <c>jsonb</c>.
    /// </summary>
    public List<CursorModelVariant> Variants { get; set; } = [];

    /// <summary>
    /// Stable display order for the picker. Lower comes first. Mirrors the
    /// SDK's natural order in <c>cursor-models.seed.json</c> so <c>default</c>
    /// (Auto) appears first, then <c>composer-2.5</c>, <c>composer-2</c>, etc.
    /// Separate from row order in the DB so admins can re-order without
    /// touching primary keys.
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

/// <summary>
/// Definition of a single tunable parameter on a <see cref="CursorModel"/>
/// (e.g. <c>reasoning</c>, <c>context</c>, <c>fast</c>). The frontend renders
/// this as a labelled dropdown / toggle in the model picker; the chosen value
/// is sent back as part of a <see cref="CursorModelVariantParam"/>.
/// </summary>
public sealed class CursorModelParameter
{
    /// <summary>Parameter id sent to the SDK, e.g. <c>reasoning</c>.</summary>
    public required string Id { get; set; }

    /// <summary>Human-readable label shown above the control.</summary>
    public required string DisplayName { get; set; }

    /// <summary>Allowed values for this parameter, in SDK-provided order.</summary>
    public List<CursorModelParameterValue> Values { get; set; } = [];
}

/// <summary>
/// One legal value for a <see cref="CursorModelParameter"/>. <see cref="Value"/>
/// is the raw SDK token; <see cref="DisplayName"/> is the optional human label
/// (e.g. <c>"272K"</c> for <c>272k</c>) — null falls back to <see cref="Value"/>.
/// </summary>
public sealed class CursorModelParameterValue
{
    public required string Value { get; set; }
    public string? DisplayName { get; set; }
}

/// <summary>
/// One pre-resolved parameter combination for a <see cref="CursorModel"/>.
/// <see cref="IsDefault"/> marks the variant the SDK suggests when no overrides
/// are supplied — exactly one variant per model carries it when the SDK
/// expresses a preference; for parameter-less models the single empty-params
/// variant is the default.
/// </summary>
public sealed class CursorModelVariant
{
    public List<CursorModelVariantParam> Params { get; set; } = [];
    public string? DisplayName { get; set; }
    public bool IsDefault { get; set; }
}

/// <summary>
/// Parameter id + chosen value pair inside a <see cref="CursorModelVariant"/>.
/// The id matches a <see cref="CursorModelParameter.Id"/> from the same model's
/// parameter set; the value matches one of that parameter's allowed values.
/// </summary>
public sealed class CursorModelVariantParam
{
    public required string Id { get; set; }
    public required string Value { get; set; }
}

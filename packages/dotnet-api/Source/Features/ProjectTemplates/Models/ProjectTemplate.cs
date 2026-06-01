using Source.Features.Projects.Models;
using Source.Shared;
using Source.Shared.Events;

namespace Source.Features.ProjectTemplates.Models;

/// <summary>
/// A curated project template (UI name: "Starter"). Pairs a GitHub template
/// repo with an optional inline V2 runtime spec, so picking a starter from the
/// new-project screen materialises a working project — repo created from the
/// template, runtime pre-wired to install deps and start services — without
/// the cold-start scaffold ritual.
///
/// <list type="bullet">
///   <item><b>GLOBAL scope</b> — there is intentionally NO <c>WorkspaceId</c>
///         on this entity. Starters are super-admin curated and shared across
///         every workspace. Mirroring <see cref="WorkspaceSpec"/> here is
///         conventions-only (<see cref="IAuditable"/> + <see cref="ISoftDelete"/>),
///         not tenancy.</item>
///   <item><b>Inline runtime spec, not an FK</b> — <see cref="RuntimeSpec"/>
///         holds a full V3 runtime-spec document as opaque JSON (jsonb). We
///         deliberately do NOT FK to <see cref="WorkspaceSpec"/> because that
///         table is workspace-scoped and starters are global. Storing the spec
///         inline keeps everything global and preserves the existing
///         snapshot-at-create-project behaviour (the project's runtime row
///         deep-copies this content at creation time).</item>
///   <item><b>Empty starter = null spec</b> — the "Empty" seed starter, and
///         any starter created without a curated runtime recipe, leaves
///         <see cref="RuntimeSpec"/> as <c>null</c>. The runtime then boots
///         with the default/empty spec, exactly as today's no-starter path.</item>
///   <item><b>Validation lives in the command layer</b> — this entity treats
///         <see cref="RuntimeSpec"/> as opaque text. The create/update commands
///         (separate card) are responsible for parsing it against
///         <c>RuntimeSpecV3</c> and rejecting invalid content before save.</item>
///   <item><b>Soft-delete + archive intent</b> — archiving a starter (per the
///         spec) is a soft delete, not a hard one. Existing projects keep
///         their historical <c>TemplateId</c> reference because the FK is
///         <c>ON DELETE SET NULL</c>; the soft-delete query filter just hides
///         the row from the picker.</item>
/// </list>
/// </summary>
public class ProjectTemplate : Entity, IAuditable, ISoftDelete
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// URL-safe identifier, globally unique. Used by the new-project picker
    /// to refer to a specific starter (e.g. <c>"empty"</c>, <c>"react-vite-ts"</c>,
    /// <c>"rails-8"</c>). Required. Max 100 chars. Unique among non-tombstoned
    /// rows via a partial index on the DB side.
    /// </summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>
    /// Human-friendly display name shown in the starter picker, e.g.
    /// <c>"React + Vite + TS"</c>. Required. Max 100 chars.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional one-line description shown beneath <see cref="Name"/> in the
    /// picker and on the admin list. Max 500 chars.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Optional icon key — a string the frontend maps to a concrete icon
    /// component (e.g. <c>"react"</c>, <c>"rails"</c>). Storing a key not a
    /// URL keeps the catalogue portable across themes / asset hosts. Max 50
    /// chars.
    /// </summary>
    public string? IconKey { get; set; }

    /// <summary>
    /// GitHub owner login (user or org) of the template repo, e.g.
    /// <c>"vitejs"</c>. Required. Max 120 chars — matches
    /// <see cref="Project.GithubRepoOwner"/> for shape parity.
    /// </summary>
    public string SourceRepoOwner { get; set; } = string.Empty;

    /// <summary>
    /// GitHub repo name of the template repo, e.g. <c>"vite"</c>. Required.
    /// Max 120 chars — matches <see cref="Project.GithubRepoName"/>.
    /// </summary>
    public string SourceRepoName { get; set; } = string.Empty;

    /// <summary>
    /// Full V2 runtime-spec document stored as <c>jsonb</c>, inline. Same
    /// shape as <c>ProjectRuntime.Spec</c> /
    /// <see cref="WorkspaceSpec.Content"/>. <c>null</c> means "no runtime
    /// recipe — the runtime boots with the default/empty spec". We do NOT FK
    /// to <see cref="WorkspaceSpec"/> here because that table is
    /// workspace-scoped and this catalogue is global; inlining keeps the
    /// snapshot-on-create-project flow untouched.
    /// </summary>
    public string? RuntimeSpec { get; set; }

    /// <summary>
    /// Whether the starter is visible in the user-facing picker. Defaults to
    /// <c>true</c>. Flipping this to <c>false</c> hides the row from the
    /// new-project screen without breaking existing projects that reference
    /// it via <c>Project.TemplateId</c>. Indexed together with
    /// <see cref="SortOrder"/> for the dominant "active starters, ordered"
    /// picker query.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// At most one starter at a time is the "default" — the new-project screen
    /// can pre-select it for users who don't deliberately pick another. The
    /// uniqueness invariant is enforced in the command layer, not the DB, so
    /// admins get a clean error instead of a <c>DbUpdateException</c>.
    /// </summary>
    public bool IsDefault { get; set; } = false;

    /// <summary>
    /// Display order in the picker — admins reorder starters so the most
    /// relevant ones surface first. Lower values sort first. Defaults to 0.
    /// </summary>
    public int SortOrder { get; set; } = 0;

    // -------- IAuditable --------
    // Auto-set by ApplicationDbContext.SaveChangesAsync; never assign manually.
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // -------- ISoftDelete --------
    // Auto-stamped on delete by ApplicationDbContext.SaveChangesAsync.
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
}

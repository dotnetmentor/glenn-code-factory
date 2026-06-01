using Source.Features.Workspaces.Models;
using Source.Shared;
using Source.Shared.Events;

namespace Source.Features.WorkspaceSpecs.Models;

/// <summary>
/// A named, reusable runtime spec scoped to a workspace. The Workspace Spec
/// Catalog lets a studio define a stack once (e.g. "fullstack-dotnet-react")
/// and stamp it onto new branches / new projects rather than re-authoring the
/// same V2 RuntimeSpec from scratch every time.
///
/// <list type="bullet">
///   <item>Snapshot semantics: forking a branch or creating a project that
///         "picks" a catalog spec <b>deep-copies</b> the spec's <see cref="Content"/>
///         into the new runtime's <c>Spec</c> field. There is no FK back from
///         <c>ProjectRuntime</c> to <see cref="WorkspaceSpec"/> — editing or
///         deleting a catalog entry never retroactively touches existing
///         branches.</item>
///   <item>Scope: catalog visibility and edit access are governed by workspace
///         membership. A member of workspace A cannot see / edit / delete
///         workspace B's catalog.</item>
///   <item><see cref="Content"/> is a full V2 RuntimeSpec JSON document stored
///         as <c>jsonb</c>. Validation is enforced at the command layer with
///         the same validator that protects per-runtime spec edits — invalid
///         content is rejected at save time.</item>
///   <item>Cascade-delete on <see cref="WorkspaceId"/>: deleting a workspace
///         deletes its catalog. Existing branches that were forked from those
///         entries are unaffected because the spec was copied in, not linked.</item>
/// </list>
///
/// <para>This card is intentionally <i>data only</i>. CRUD commands /
/// controllers / validation / system-seeding all live in follow-up cards.</para>
/// </summary>
public class WorkspaceSpec : Entity, IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The workspace this catalog entry belongs to. FK to <see cref="Workspace"/>.
    /// Indexed for the dominant "list all specs in workspace X" lookup, and
    /// combined with <see cref="Name"/> in a unique composite index to prevent
    /// two catalog entries sharing a name within the same workspace.
    /// </summary>
    public Guid WorkspaceId { get; set; }

    /// <summary>Navigation to the owning workspace. Nullable to mirror the
    /// no-eager-load convention used elsewhere — handlers explicitly Include
    /// when they need it.</summary>
    public Workspace? Workspace { get; set; }

    /// <summary>
    /// Human-friendly catalog name, e.g. <c>"fullstack-dotnet-react"</c>. Unique
    /// within a workspace. Max 100 chars.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional one-line description shown in the catalog management UI, e.g.
    /// <c>"Standard backend + frontend for our customer projects."</c>. Max 500 chars.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Full V2 RuntimeSpec document stored as <c>jsonb</c>. Same shape as
    /// <c>ProjectRuntime.Spec</c> at <see cref="Source.Features.RuntimeLifecycle.Models.ProjectRuntime.Spec"/>
    /// — freeform <c>install</c> / <c>services[]</c> / <c>setup</c>. Validated
    /// against the V2 schema at command-handler time; invalid content is
    /// rejected with a structured error and no row is written.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// The user who originally created this catalog entry. Stored as a plain
    /// <see cref="string"/> (no EF-configured FK) — the ASP.NET Identity
    /// <c>User</c> PK is a string, so we record the user id as a string value
    /// without a relational FK to keep the catalog decoupled from the auth
    /// slice. Mirrors the <c>OwnerId</c> / <c>UserId</c> convention used by
    /// every other entity that references a user.
    /// </summary>
    public string CreatedByUserId { get; set; } = string.Empty;

    /// <summary>
    /// The user who last edited this catalog entry. Same plain-<see cref="string"/>
    /// convention as <see cref="CreatedByUserId"/>. Surfaced in the catalog
    /// management UI as "last edited by" — see Scene 5 of the spec.
    /// </summary>
    public string UpdatedByUserId { get; set; } = string.Empty;

    // -------- IAuditable --------
    // Auto-set by ApplicationDbContext.SaveChangesAsync; never assign manually.
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

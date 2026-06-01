using Source.Shared;
using Source.Shared.Events;

namespace Source.Features.Projects.Models;

/// <summary>
/// Project-scoped override of the system-wide Agent SDK permission defaults.
///
/// <para>
/// Lifecycle invariant: <b>presence of the row is the override</b>. If a
/// <see cref="ProjectAgentPermissions"/> row exists for a given
/// <see cref="ProjectId"/>, the daemon's
/// <c>IAgentPermissionsResolver.ResolveForProjectAsync</c> returns these
/// values verbatim — <b>no merging</b> with the system catalog. If the row
/// is absent, the resolver falls through to the system defaults stored in
/// <c>SystemSettings</c> under the <c>AgentPermissions</c> category. Deleting
/// the row is the supported "stop overriding" gesture.
/// </para>
///
/// <list type="bullet">
///   <item><see cref="ProjectId"/> is a real FK to <see cref="Project"/>,
///         backed by a <b>unique</b> index so the 1:0..1 relationship is
///         enforced at the database level — you cannot accidentally write
///         two override rows for the same project.</item>
///   <item><see cref="PermissionMode"/> mirrors the SDK enum
///         (<c>default | acceptEdits | bypassPermissions | plan | dontAsk</c>).
///         Stored as a string so adding a future mode doesn't require a
///         schema migration. <c>auto</c> is intentionally excluded — see the
///         spec's "Non-Goals".</item>
///   <item><see cref="AllowDangerouslySkipPermissions"/> is the SDK's
///         opt-in switch; the spec requires it to be true whenever
///         <see cref="PermissionMode"/> is <c>bypassPermissions</c>, but
///         we don't encode that constraint at the entity level — the
///         command-handler layer owns that validation.</item>
///   <item><see cref="AllowedTools"/>, <see cref="DisallowedTools"/> and
///         <see cref="AdditionalDirectories"/> are stored as Postgres
///         <c>jsonb</c> arrays (Npgsql's dynamic-JSON serialiser is enabled
///         in <c>DatabaseExtensions</c>, so plain <see cref="List{T}"/> of
///         <see cref="string"/> round-trips natively). <c>jsonb</c> over
///         <c>text[]</c> keeps the storage shape symmetric with how the
///         system catalog stores the same lists — one less impedance
///         mismatch when the resolver swaps between the two sources.</item>
///   <item>Auditable but <b>not</b> soft-deletable: this row is config, not
///         data. When a project owner stops overriding, the row is hard-
///         deleted; cascade from <see cref="Project"/> handles project
///         hard-deletes. (Soft-deleting a project just leaves the override
///         row in place, which is fine — nothing reads it while the project
///         is soft-deleted.)</item>
/// </list>
/// </summary>
public class ProjectAgentPermissions : Entity, IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// FK to the owning <see cref="Project"/>. Required and unique — the
    /// uniqueness invariant is what makes this a 1:0..1 relationship rather
    /// than 1:n.
    /// </summary>
    public Guid ProjectId { get; set; }

    /// <summary>Navigation to the owning project.</summary>
    public Project Project { get; set; } = null!;

    /// <summary>
    /// SDK permission mode. One of
    /// <c>default | acceptEdits | bypassPermissions | plan | dontAsk</c>.
    /// Stored as a string; validation of the enum value happens in the
    /// command handler that writes the row.
    /// </summary>
    public string PermissionMode { get; set; } = string.Empty;

    /// <summary>
    /// The SDK's <c>allowDangerouslySkipPermissions</c> opt-in. Must be
    /// <c>true</c> whenever <see cref="PermissionMode"/> is
    /// <c>bypassPermissions</c>; the command handler enforces that pairing.
    /// </summary>
    public bool AllowDangerouslySkipPermissions { get; set; }

    /// <summary>
    /// Allow-list of tool patterns (e.g. <c>Read</c>, <c>Bash(npm test)</c>).
    /// Stored as <c>jsonb</c>. Never null — empty list means "no allow-list
    /// entries", which is different from "no override row" (the latter
    /// falls back to system defaults entirely).
    /// </summary>
    public List<string> AllowedTools { get; set; } = new();

    /// <summary>
    /// Disallow-list of tool patterns. Wins over
    /// <c>bypassPermissions</c> per the SDK semantics. Stored as
    /// <c>jsonb</c>. Never null.
    /// </summary>
    public List<string> DisallowedTools { get; set; } = new();

    /// <summary>
    /// Absolute paths the agent may write to <i>in addition to</i> the
    /// project <c>cwd</c>. Stored as <c>jsonb</c>. Never null.
    /// </summary>
    public List<string> AdditionalDirectories { get; set; } = new();

    // -------- IAuditable --------
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

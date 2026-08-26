using Source.Shared;
using Source.Shared.Events;

namespace Source.Features.RuntimeTemplates.Models;

/// <summary>
/// Catalog row for a golden template box — the prepared Box VM every runtime is
/// forked from. The template-build script (<c>scripts/build-box-template.sh</c>)
/// creates a box, installs the full runtime stack (Node, supervisord units,
/// postgres, mise, Playwright, the daemon bootstrap), stops it so Box snapshots
/// the disk, and registers a row here. The main API reads this table to know:
///
/// <list type="bullet">
///   <item>which template boxes currently exist;</item>
///   <item>which one is the default fork source (latest <c>Active</c> by
///         <see cref="BuiltAt"/>);</item>
///   <item>which ones have been deprecated or yanked.</item>
/// </list>
///
/// <para>Deliberately NOT soft-deletable — yanked templates stay in the table so
/// the audit trail of what was once published survives. Kept-as-POCO style
/// matches <c>BoxOperation</c>; behaviour lives in handlers, not on the entity.</para>
/// </summary>
public class RuntimeTemplate : Entity, IAuditable
{
    public Guid Id { get; set; }

    /// <summary>
    /// The template box's id on Box. Unique — registering the same box twice is
    /// a mistake; the unique constraint is the natural idempotency key for the
    /// registration endpoint. The box must stay stopped (archived): forks
    /// always take the latest snapshot, and an accidentally-running template
    /// would both bill and drift from what was validated.
    /// </summary>
    public string BoxId { get; set; } = string.Empty;

    /// <summary>Human-readable label, e.g. <c>"base-2026.08.20-7af3b21"</c>.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Git commit of the platform repo the template was built from. Free-form short or long SHA.</summary>
    public string GitSha { get; set; } = string.Empty;

    /// <summary>UTC timestamp the template finished building (reported by the build script).</summary>
    public DateTime BuiltAt { get; set; }

    /// <summary>Lifecycle state — see <see cref="RuntimeTemplateStatus"/>. Persisted as a string.</summary>
    public RuntimeTemplateStatus Status { get; set; } = RuntimeTemplateStatus.Active;

    /// <summary>Free-form operator note, e.g. why a template was deprecated or yanked.</summary>
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

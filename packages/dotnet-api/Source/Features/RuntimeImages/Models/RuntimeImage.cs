using Source.Shared;
using Source.Shared.Events;

namespace Source.Features.RuntimeImages.Models;

/// <summary>
/// Catalog row for a single published runtime base image — the image every
/// Machine boots from. CI registers a new row each time it pushes a build to
/// the registry, and the main API reads this table to know:
///
/// <list type="bullet">
///   <item>which images currently exist;</item>
///   <item>which one is the default spawn target (latest <c>Active</c> by
///         <see cref="BuiltAt"/>);</item>
///   <item>which ones have been deprecated or yanked.</item>
/// </list>
///
/// <para>Deliberately NOT soft-deletable — yanked images stay in the table so
/// the audit trail of what was once published survives. The kept-as-POCO style
/// matches <c>FlyOperation</c>; behaviour lives in handlers, not on the entity.</para>
/// </summary>
public class RuntimeImage : Entity, IAuditable
{
    public Guid Id { get; set; }

    /// <summary>
    /// Human-readable image tag, e.g. <c>"2026.05.08-7af3b21"</c>. Unique — CI
    /// must never publish the same tag twice. The unique constraint is the
    /// natural idempotency key for the registration endpoint.
    /// </summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>
    /// Content-addressable SHA256 digest of the image (e.g. <c>"sha256:abc..."</c>).
    /// What we actually pin machines to — tags can in theory be moved, digests cannot.
    /// </summary>
    public string Digest { get; set; } = string.Empty;

    /// <summary>
    /// Fully-qualified registry path, e.g. <c>"registry.fly.io/fwd-runtime"</c>.
    /// Stored alongside the digest so consumers can build a pull reference
    /// without inferring it from environment.
    /// </summary>
    public string Registry { get; set; } = string.Empty;

    /// <summary>Git commit the image was built from. Free-form short or long SHA.</summary>
    public string GitSha { get; set; } = string.Empty;

    /// <summary>UTC timestamp the image was built (reported by CI, not by us).</summary>
    public DateTime BuiltAt { get; set; }

    /// <summary>Approximate image size in megabytes. Useful for cost/perf dashboards.</summary>
    public int SizeMb { get; set; }

    /// <summary>Lifecycle state — see <see cref="RuntimeImageStatus"/>. Persisted as a string.</summary>
    public RuntimeImageStatus Status { get; set; } = RuntimeImageStatus.Active;

    /// <summary>Free-form operator note, e.g. why an image was deprecated or yanked.</summary>
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

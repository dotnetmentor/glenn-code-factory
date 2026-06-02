using Source.Shared;
using Source.Shared.Events;

namespace Source.Features.DaemonVersions.Models;

/// <summary>
/// A versioned daemon bundle (tarball + sha256) hosted in <c>IFileStorageService</c>
/// (Cloudflare R2 in prod, local in dev). Runtime containers download the bundle
/// pinned to their <see cref="Channel"/> on cold-boot, verify
/// <see cref="BundleSha256"/>, extract and exec — so we can iterate daemon code
/// without rebuilding the runtime Docker image.
///
/// <list type="bullet">
///   <item><see cref="Channel"/> partitions versions (today only <c>"stable"</c>;
///         future channels: <c>beta</c>, <c>canary</c>). Exactly one row per
///         channel is <see cref="IsActive"/>=<c>true</c>; publishing a new
///         version atomically deactivates the previous active row in the same
///         channel.</item>
///   <item><see cref="Version"/> is auto-generated as
///         <c>{yyyy.MM.dd.HHmmss}</c> at publish time — no git dependency, just
///         a monotonically increasing timestamp string.</item>
///   <item><see cref="BundleStorageKey"/> is the relative path returned by
///         <c>IFileStorageService.SaveFileAsync</c>; <c>GetFileUrlAsync</c>
///         turns it back into a public URL.</item>
///   <item>Auditable, but NOT soft-deletable — old versions stay around as
///         immutable history (cheap rows, useful for forensics + rollback).</item>
/// </list>
/// </summary>
public class DaemonVersion : Entity, IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Auto-generated version string, unique per channel. Format:
    /// <c>{yyyy.MM.dd.HHmmss}</c> (UTC). Example: <c>2026.05.10.143012</c>.
    /// Required, max 64 chars.
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Release channel. Defaults to <c>"stable"</c>. Today only "stable" is
    /// used; future-proofing for beta/canary. Required, max 32 chars.
    /// </summary>
    public string Channel { get; set; } = "stable";

    /// <summary>
    /// Storage path returned by <c>IFileStorageService.SaveFileAsync</c>. Use
    /// <c>GetFileUrlAsync</c> to resolve to a public URL at read time so the
    /// stored value stays portable across storage backends (R2 vs local). Max
    /// 1024 chars.
    /// </summary>
    public string BundleStorageKey { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 of the tarball, lowercase hex (64 chars). The runtime
    /// bootstrap script verifies this before extracting the bundle —
    /// mismatched hash means a corrupted download or a substituted bundle.
    /// </summary>
    public string BundleSha256 { get; set; } = string.Empty;

    /// <summary>Size of the tarball in bytes.</summary>
    public long BundleSizeBytes { get; set; }

    /// <summary>Free-form release notes. Max 2000 chars.</summary>
    public string? Notes { get; set; }

    /// <summary>Source commit this bundle was built from. Max 40 chars (full git SHA).</summary>
    public string? GitSha { get; set; }

    /// <summary>UTC timestamp the version was released (= the publish call).</summary>
    public DateTime ReleasedAt { get; set; }

    /// <summary>
    /// Exactly one row per <see cref="Channel"/> may have <c>IsActive=true</c>
    /// at any time — that's the row <see cref="Queries.ResolveDaemonVersion.ResolveDaemonVersionQuery"/>
    /// returns. The publish command flips the previous active row off and the
    /// new row on inside a single SaveChanges transaction.
    /// </summary>
    public bool IsActive { get; set; }

    // -------- IAuditable --------
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

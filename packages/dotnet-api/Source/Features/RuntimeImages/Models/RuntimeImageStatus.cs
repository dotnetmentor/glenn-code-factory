namespace Source.Features.RuntimeImages.Models;

/// <summary>
/// Lifecycle state of a published runtime base image. Stored as a string in the
/// database (see <c>ApplicationDbContext</c> configuration) so adding new
/// states later doesn't break existing rows.
/// </summary>
public enum RuntimeImageStatus
{
    /// <summary>Image is healthy and eligible to be the default spawn target.</summary>
    Active,

    /// <summary>
    /// Still bootable and usable for existing machines, but no longer the
    /// preferred default — operators should plan to roll forward.
    /// </summary>
    Deprecated,

    /// <summary>
    /// Withdrawn — must not be used to spawn new machines. Rows are kept (not
    /// deleted) so we retain the audit trail of what was once published.
    /// </summary>
    Yanked,
}

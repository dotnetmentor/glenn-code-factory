using System.ComponentModel.DataAnnotations;
using Source.Shared;

namespace Source.Features.Waitlist.Models;

/// <summary>
/// A single interest signup captured from the public marketing landing page.
/// Anonymous + auditable: <see cref="CreatedAt"/>/<see cref="UpdatedAt"/> are
/// auto-set by <c>ApplicationDbContext.SaveChangesAsync</c> via <see cref="IAuditable"/> —
/// never set them by hand.
///
/// <para>Email is stored normalized (lower-cased, trimmed) and carries a unique
/// index so re-submitting the same address is a no-op rather than a duplicate row;
/// the join handler treats a hit as an idempotent success.</para>
/// </summary>
public class WaitlistSignup : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(256)]
    public string Email { get; set; } = string.Empty;

    /// <summary>Where the signup originated, e.g. <c>"landing-preview"</c>. Free-form, capped.</summary>
    [MaxLength(50)]
    public string? Source { get; set; }

    /// <summary>Optional "what would you build?" line the visitor typed.</summary>
    [MaxLength(500)]
    public string? Note { get; set; }

    /// <summary>Best-effort UA string for light analytics. Truncated before persist.</summary>
    [MaxLength(500)]
    public string? UserAgent { get; set; }

    /// <summary>Best-effort referrer for light analytics. Truncated before persist.</summary>
    [MaxLength(500)]
    public string? Referrer { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

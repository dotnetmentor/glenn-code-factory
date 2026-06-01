using System.ComponentModel.DataAnnotations;

namespace Source.Features.ErrorLog.Models;

/// <summary>
/// An aggregated error signature — one row per unique fingerprint, updated in place
/// as occurrences arrive. This is the primary dashboard view for the error pipeline;
/// individual <see cref="ErrorLog"/> rows are samples that roll up into a signature.
///
/// Hash is a 64-char lowercase hex SHA256 produced by <c>IErrorSignatureHasher</c>
/// over exception type + top 3 stack frames + source.
/// </summary>
public class ErrorSignature
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(64)]
    [Required]
    public string Hash { get; set; } = string.Empty;

    [MaxLength(50)]
    [Required]
    public string Source { get; set; } = string.Empty;

    [MaxLength(20)]
    [Required]
    public string Severity { get; set; } = string.Empty;

    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

    public long Count { get; set; }

    public bool IsResolved { get; set; }

    public DateTime? ResolvedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

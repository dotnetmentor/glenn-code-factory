using System.ComponentModel.DataAnnotations;

namespace Source.Features.ErrorLog.Models;

public class ErrorLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    [MaxLength(2000)]

    [Required]

    public string Message { get; set; } = string.Empty;
    

    public string? StackTrace { get; set; }
    
    [MaxLength(50)]

    [Required]

    public string Source { get; set; } = string.Empty;
    
    [MaxLength(20)]

    [Required]

    public string Severity { get; set; } = string.Empty;
    
    [MaxLength(100)]

    public string? CorrelationId { get; set; }
    
    [MaxLength(500)]

    public string? RequestPath { get; set; }
    
    [MaxLength(10)]

    public string? RequestMethod { get; set; }
    

    public string? ContextData { get; set; }
    

    public bool? IsResolved { get; set; }


    public DateTime? ResolvedAt { get; set; }

    /// <summary>
    /// FK to the aggregated <see cref="ErrorSignature"/> row this occurrence rolls up into.
    /// Nullable because (a) existing pre-migration rows have no signature, and
    /// (b) the signature is populated lazily by the persistence worker after hashing.
    /// </summary>
    public Guid? SignatureId { get; set; }

    public ErrorSignature? Signature { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

}


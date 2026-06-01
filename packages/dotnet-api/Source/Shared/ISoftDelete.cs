namespace Source.Shared;

/// <summary>
/// Entities implementing this get soft-delete fields set automatically
/// by ApplicationDbContext.SaveChangesAsync when IsDeleted is flipped to true.
/// Global query filters exclude deleted records automatically.
/// </summary>
public interface ISoftDelete
{
    bool IsDeleted { get; set; }
    DateTime? DeletedAt { get; set; }
    string? DeletedBy { get; set; }
}

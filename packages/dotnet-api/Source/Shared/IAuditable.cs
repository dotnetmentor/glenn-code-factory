namespace Source.Shared;

/// <summary>
/// Entities implementing this get CreatedAt/UpdatedAt set automatically
/// by ApplicationDbContext.SaveChangesAsync. Never set these manually.
/// </summary>
public interface IAuditable
{
    DateTime CreatedAt { get; set; }
    DateTime UpdatedAt { get; set; }
}

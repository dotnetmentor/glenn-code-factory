using Source.Shared;
using Source.Shared.Events;

namespace Source.Features.Workspaces.Models;

/// <summary>
/// Per-workspace envelope-encryption key material. Mirrors
/// <see cref="ProjectSecrets.Models.ProjectKeyMaterial"/> for workspace-scoped BYOK.
/// </summary>
public class WorkspaceKeyMaterial : Entity, IAuditable, ISoftDelete
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WorkspaceId { get; set; }

    public byte[] WrappedDek { get; set; } = Array.Empty<byte>();

    public int MasterKeyVersion { get; set; } = 1;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
}

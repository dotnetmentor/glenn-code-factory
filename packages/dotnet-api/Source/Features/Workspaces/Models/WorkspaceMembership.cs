using Source.Features.Users.Models;
using Source.Shared;
using Source.Shared.Events;

namespace Source.Features.Workspaces.Models;

public class WorkspaceMembership : Entity, IAuditable
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Workspace Workspace { get; set; } = null!;

    public string UserId { get; set; } = string.Empty;
    public User User { get; set; } = null!;

    public WorkspaceRole Role { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

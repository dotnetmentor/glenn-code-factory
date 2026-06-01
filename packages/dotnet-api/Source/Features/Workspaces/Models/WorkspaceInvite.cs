using Source.Features.Users.Models;
using Source.Shared;
using Source.Shared.Events;

namespace Source.Features.Workspaces.Models;

public class WorkspaceInvite : Entity, IAuditable
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }
    public Workspace Workspace { get; set; } = null!;

    /// <summary>The invitee's email address (case-insensitive at the DB level).</summary>
    public string Email { get; set; } = string.Empty;

    public WorkspaceRole Role { get; set; } = WorkspaceRole.Member;

    public string InvitedById { get; set; } = string.Empty;
    public User InvitedBy { get; set; } = null!;

    /// <summary>URL-safe random token used as the accept-link secret. Globally unique.</summary>
    public string Token { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public string? AcceptedByUserId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public bool IsPending => AcceptedAt is null && ExpiresAt > DateTime.UtcNow;
    public bool IsExpired => AcceptedAt is null && ExpiresAt <= DateTime.UtcNow;
}

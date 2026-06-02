using Source.Features.Users.Models;
using Source.Features.Workspaces.Events;
using Source.Shared;
using Source.Shared.Events;
using Source.Shared.Results;

namespace Source.Features.Workspaces.Models;

public class Workspace : Entity, IAuditable, ISoftDelete
{
    public Guid Id { get; set; }

    /// <summary>
    /// URL-safe lowercase kebab identifier. Globally unique. Max 60 chars.
    /// </summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>
    /// Human-friendly display name. Max 120 chars.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// FK to <see cref="User"/>.Id of the workspace owner. Always also has an Owner-role
    /// <see cref="WorkspaceMembership"/>; this denormalised pointer is for fast lookups
    /// and to short-circuit "last owner" protection logic.
    /// </summary>
    public string OwnerId { get; set; } = string.Empty;

    public User Owner { get; set; } = null!;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }

    /// <summary>BYOK Cursor API key for all projects in this workspace, encrypted under the workspace DEK.</summary>
    public string? EncryptedCursorApiKey { get; set; }

    /// <summary>
    /// When false, projects cannot set their own <see cref="Projects.Models.Project.EncryptedCursorApiKey"/>.
    /// They inherit this workspace key (and the host env var fallback).
    /// </summary>
    public bool AllowProjectCursorApiKeyOverride { get; set; } = true;

    public ICollection<WorkspaceMembership> Memberships { get; set; } = new List<WorkspaceMembership>();
    public ICollection<WorkspaceInvite> Invites { get; set; } = new List<WorkspaceInvite>();

    public Result SetEncryptedCursorApiKey(string? envelope)
    {
        if (string.Equals(EncryptedCursorApiKey, envelope, StringComparison.Ordinal))
        {
            return Result.Success();
        }

        EncryptedCursorApiKey = envelope;
        return Result.Success();
    }

    public Result SetAllowProjectCursorApiKeyOverride(bool allow)
    {
        if (AllowProjectCursorApiKeyOverride == allow)
        {
            return Result.Success();
        }

        AllowProjectCursorApiKeyOverride = allow;
        return Result.Success();
    }

    /// <summary>Raise this when a workspace is freshly created (after the owner membership exists).</summary>
    public void MarkCreated()
    {
        RaiseDomainEvent(new WorkspaceCreated(Id, Slug, Name, OwnerId));
    }

    public Result Rename(string newName, string newSlug)
    {
        if (string.IsNullOrWhiteSpace(newName)) return Result.Failure("Name is required");
        if (string.IsNullOrWhiteSpace(newSlug)) return Result.Failure("Slug is required");

        Name = newName.Trim();
        Slug = newSlug.Trim().ToLowerInvariant();
        RaiseDomainEvent(new WorkspaceRenamed(Id, Slug, Name));
        return Result.Success();
    }

    /// <summary>Raise the role-changed event after the membership row has been mutated.</summary>
    public void RecordMemberRoleChanged(string userId, WorkspaceRole oldRole, WorkspaceRole newRole)
    {
        RaiseDomainEvent(new WorkspaceMemberRoleChanged(Id, userId, oldRole, newRole));
    }

    /// <summary>Raise the member-removed event after the membership row has been deleted.</summary>
    public void RecordMemberRemoved(string userId)
    {
        RaiseDomainEvent(new WorkspaceMemberRemoved(Id, userId));
    }

    /// <summary>Raise the member-added event after the membership row has been inserted.</summary>
    public void RecordMemberAdded(string userId, WorkspaceRole role)
    {
        RaiseDomainEvent(new WorkspaceMemberAdded(Id, userId, role));
    }

    /// <summary>Raise the invite-created event after the invite row has been added.</summary>
    public void RaiseInviteCreated(Guid inviteId, string email, WorkspaceRole role)
    {
        RaiseDomainEvent(new WorkspaceInviteCreated(inviteId, Id, email, role));
    }

    /// <summary>Raise the invite-accepted event after the membership row has been added.</summary>
    public void RaiseInviteAccepted(Guid inviteId, string userId)
    {
        RaiseDomainEvent(new WorkspaceInviteAccepted(inviteId, Id, userId));
    }
}

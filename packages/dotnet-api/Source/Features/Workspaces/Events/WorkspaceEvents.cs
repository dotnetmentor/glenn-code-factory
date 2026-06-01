using Source.Features.Workspaces.Models;
using Source.Shared.Events;

namespace Source.Features.Workspaces.Events;

public record WorkspaceCreated(
    Guid WorkspaceId,
    string Slug,
    string Name,
    string OwnerUserId,
    DateTime OccurredAt
) : IEntityDomainEvent
{
    public WorkspaceCreated(Guid workspaceId, string slug, string name, string ownerUserId)
        : this(workspaceId, slug, name, ownerUserId, DateTime.UtcNow) { }

    string IEntityDomainEvent.EntityId => WorkspaceId.ToString();
    string IEntityDomainEvent.EntityType => "Workspace";
}

public record WorkspaceRenamed(
    Guid WorkspaceId,
    string Slug,
    string Name,
    DateTime OccurredAt
) : IEntityDomainEvent
{
    public WorkspaceRenamed(Guid workspaceId, string slug, string name)
        : this(workspaceId, slug, name, DateTime.UtcNow) { }

    string IEntityDomainEvent.EntityId => WorkspaceId.ToString();
    string IEntityDomainEvent.EntityType => "Workspace";
}

public record WorkspaceMemberAdded(
    Guid WorkspaceId,
    string UserId,
    WorkspaceRole Role,
    DateTime OccurredAt
) : IEntityDomainEvent
{
    public WorkspaceMemberAdded(Guid workspaceId, string userId, WorkspaceRole role)
        : this(workspaceId, userId, role, DateTime.UtcNow) { }

    string IEntityDomainEvent.EntityId => WorkspaceId.ToString();
    string IEntityDomainEvent.EntityType => "Workspace";
}

public record WorkspaceMemberRoleChanged(
    Guid WorkspaceId,
    string UserId,
    WorkspaceRole OldRole,
    WorkspaceRole NewRole,
    DateTime OccurredAt
) : IEntityDomainEvent
{
    public WorkspaceMemberRoleChanged(Guid workspaceId, string userId, WorkspaceRole oldRole, WorkspaceRole newRole)
        : this(workspaceId, userId, oldRole, newRole, DateTime.UtcNow) { }

    string IEntityDomainEvent.EntityId => WorkspaceId.ToString();
    string IEntityDomainEvent.EntityType => "Workspace";
}

public record WorkspaceMemberRemoved(
    Guid WorkspaceId,
    string UserId,
    DateTime OccurredAt
) : IEntityDomainEvent
{
    public WorkspaceMemberRemoved(Guid workspaceId, string userId)
        : this(workspaceId, userId, DateTime.UtcNow) { }

    string IEntityDomainEvent.EntityId => WorkspaceId.ToString();
    string IEntityDomainEvent.EntityType => "Workspace";
}

public record WorkspaceInviteCreated(
    Guid InviteId,
    Guid WorkspaceId,
    string Email,
    WorkspaceRole Role,
    DateTime OccurredAt
) : IEntityDomainEvent
{
    public WorkspaceInviteCreated(Guid inviteId, Guid workspaceId, string email, WorkspaceRole role)
        : this(inviteId, workspaceId, email, role, DateTime.UtcNow) { }

    string IEntityDomainEvent.EntityId => InviteId.ToString();
    string IEntityDomainEvent.EntityType => "WorkspaceInvite";
}

public record WorkspaceInviteAccepted(
    Guid InviteId,
    Guid WorkspaceId,
    string UserId,
    DateTime OccurredAt
) : IEntityDomainEvent
{
    public WorkspaceInviteAccepted(Guid inviteId, Guid workspaceId, string userId)
        : this(inviteId, workspaceId, userId, DateTime.UtcNow) { }

    string IEntityDomainEvent.EntityId => InviteId.ToString();
    string IEntityDomainEvent.EntityType => "WorkspaceInvite";
}

using Source.Shared.Events;

namespace Source.Features.Projects.Events;

public record ProjectCreated(
    Guid ProjectId,
    Guid WorkspaceId,
    string OwnerUserId,
    string Name,
    string GithubRepoOwner,
    string GithubRepoName,
    Guid GithubInstallationId,
    DateTime OccurredAt
) : IEntityDomainEvent
{
    public ProjectCreated(
        Guid projectId,
        Guid workspaceId,
        string ownerUserId,
        string name,
        string githubRepoOwner,
        string githubRepoName,
        Guid githubInstallationId)
        : this(projectId, workspaceId, ownerUserId, name, githubRepoOwner, githubRepoName, githubInstallationId, DateTime.UtcNow) { }

    string IEntityDomainEvent.EntityId => ProjectId.ToString();
    string IEntityDomainEvent.EntityType => "Project";
}

public record ProjectRenamed(
    Guid ProjectId,
    string OldName,
    string NewName,
    DateTime OccurredAt
) : IEntityDomainEvent
{
    public ProjectRenamed(Guid projectId, string oldName, string newName)
        : this(projectId, oldName, newName, DateTime.UtcNow) { }

    string IEntityDomainEvent.EntityId => ProjectId.ToString();
    string IEntityDomainEvent.EntityType => "Project";
}

public record ProjectDeleted(
    Guid ProjectId,
    DateTime OccurredAt
) : IEntityDomainEvent
{
    public ProjectDeleted(Guid projectId)
        : this(projectId, DateTime.UtcNow) { }

    string IEntityDomainEvent.EntityId => ProjectId.ToString();
    string IEntityDomainEvent.EntityType => "Project";
}

public record ProjectDefaultModelChanged(
    Guid ProjectId,
    Guid? OldModelId,
    Guid? NewModelId,
    DateTime OccurredAt
) : IEntityDomainEvent
{
    public ProjectDefaultModelChanged(Guid projectId, Guid? oldModelId, Guid? newModelId)
        : this(projectId, oldModelId, newModelId, DateTime.UtcNow) { }

    string IEntityDomainEvent.EntityId => ProjectId.ToString();
    string IEntityDomainEvent.EntityType => "Project";
}

public record BranchCopied(
    Guid NewBranchId,
    Guid SourceBranchId,
    Guid NewRuntimeId,
    string ForkedVolumeId,
    DateTime OccurredAt
) : IEntityDomainEvent
{
    public BranchCopied(Guid newBranchId, Guid sourceBranchId, Guid newRuntimeId, string forkedVolumeId)
        : this(newBranchId, sourceBranchId, newRuntimeId, forkedVolumeId, DateTime.UtcNow) { }

    string IEntityDomainEvent.EntityId => NewBranchId.ToString();
    string IEntityDomainEvent.EntityType => "ProjectBranch";
}

public record ProjectBranchArchived(
    Guid BranchId,
    Guid ProjectId,
    string Name,
    DateTime OccurredAt
) : IEntityDomainEvent
{
    public ProjectBranchArchived(Guid branchId, Guid projectId, string name)
        : this(branchId, projectId, name, DateTime.UtcNow) { }

    string IEntityDomainEvent.EntityId => BranchId.ToString();
    string IEntityDomainEvent.EntityType => "ProjectBranch";
}

public record ProjectBranchUnarchived(
    Guid BranchId,
    Guid ProjectId,
    string Name,
    DateTime OccurredAt
) : IEntityDomainEvent
{
    public ProjectBranchUnarchived(Guid branchId, Guid projectId, string name)
        : this(branchId, projectId, name, DateTime.UtcNow) { }

    string IEntityDomainEvent.EntityId => BranchId.ToString();
    string IEntityDomainEvent.EntityType => "ProjectBranch";
}

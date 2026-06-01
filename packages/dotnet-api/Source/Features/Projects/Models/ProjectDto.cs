using Source.Features.RuntimeLifecycle.Models;

namespace Source.Features.Projects.Models;

public record ProjectDto(
    Guid Id,
    string Name,
    Guid WorkspaceId,
    Guid DefaultBranchId,
    string DefaultBranchName,
    Guid RuntimeId,
    RuntimeState RuntimeState,
    string GithubRepoOwner,
    string GithubRepoName,
    Guid? GithubInstallationId,
    int PreviewPort,
    string RuntimeCpuKind,
    int RuntimeCpus,
    int RuntimeMemoryMb,
    int RuntimeVolumeSizeGb,
    Guid? ModelId,
    string? ModelSlug);

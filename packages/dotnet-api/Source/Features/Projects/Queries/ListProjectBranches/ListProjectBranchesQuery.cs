using Source.Features.Projects.Models;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Queries.ListProjectBranches;

/// <summary>
/// Read query for <c>GET /api/projects/{projectId}/branches</c> — returns the
/// flat list of <see cref="ProjectBranchDto"/> rows the project workspace
/// shell page needs to populate its branch picker.
///
/// <para>Same membership gate as <see cref="GetProject.GetProjectQuery"/>:
/// missing project, soft-deleted project and non-member callers all collapse
/// to a single <c>not-found:</c> failure so the controller can return 404
/// without leaking project existence.</para>
/// </summary>
public sealed record ListProjectBranchesQuery(
    Guid ProjectId,
    string CallerUserId,
    bool IncludeArchived = false
) : IQuery<Result<List<ProjectBranchDto>>>;

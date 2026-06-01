using Source.Features.Projects.Models;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Queries.GetProject;

/// <summary>
/// Read query for the project workspace shell page (<c>GET /api/projects/{id}</c>) —
/// returns the same <see cref="ProjectDto"/> shape the
/// <c>POST /api/projects</c> onboarding atom emits, so the frontend can hydrate
/// the same view model regardless of whether it just created the project or is
/// loading it cold from a deep link.
///
/// <para>Authorization is encoded in the handler:</para>
/// <list type="bullet">
///   <item>caller must be a member of the project's workspace (the owner is a
///         member of their own workspace by construction);</item>
///   <item>missing project, soft-deleted project, and non-member caller all
///         collapse to a single <c>not-found:</c> failure so the controller can
///         return 404 without leaking project existence — same pattern as
///         <c>UpdateProjectByokHandler</c>.</item>
/// </list>
/// </summary>
public sealed record GetProjectQuery(
    Guid ProjectId,
    string CallerUserId
) : IQuery<Result<ProjectDto>>;

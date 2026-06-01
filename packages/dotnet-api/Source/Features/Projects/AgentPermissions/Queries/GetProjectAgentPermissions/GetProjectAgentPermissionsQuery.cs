using Source.Features.Projects.AgentPermissions.Models;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.AgentPermissions.Queries.GetProjectAgentPermissions;

/// <summary>
/// Read query for the project Agent SDK permission override row, used by the
/// project settings UI at <c>/w/{slug}/projects/{id}/settings/agent-permissions</c>.
///
/// <para><b>Tri-state result.</b> The query distinguishes three outcomes:</para>
/// <list type="bullet">
///   <item><b>Success with non-null DTO</b> — an override row exists; the
///         settings UI renders the editor populated with those values and
///         shows the "override is on" toggle.</item>
///   <item><b>Success with null DTO</b> — the project exists and the caller
///         can see it, but no override row is present. The settings UI shows
///         the toggle in the "off" position; the daemon will fall through to
///         the system defaults at turn time.</item>
///   <item><b>Failure with <see cref="GetProjectAgentPermissionsHandler.NotFoundPrefix"/></b>
///         — the project doesn't exist or the caller isn't a member of the
///         workspace. The controller maps this to 404 so existence cannot be
///         probed — same shape as <c>GetProjectHandler</c>.</item>
/// </list>
/// </summary>
public sealed record GetProjectAgentPermissionsQuery(
    Guid ProjectId,
    string CallerUserId
) : IQuery<Result<ProjectAgentPermissionsDto?>>;

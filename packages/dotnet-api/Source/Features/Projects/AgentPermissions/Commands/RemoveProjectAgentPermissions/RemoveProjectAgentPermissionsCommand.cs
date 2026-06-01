using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.AgentPermissions.Commands.RemoveProjectAgentPermissions;

/// <summary>
/// Deletes a project's Agent SDK permission override row. The supported
/// "stop overriding" gesture — after this command lands, the resolver falls
/// through to the system defaults at the next turn.
///
/// <para><b>Idempotent.</b> Removing a non-existent override row is a no-op
/// success, not a failure. This matches the spec ("Deleting the row is the
/// supported 'stop overriding' gesture") and avoids racing two settings-page
/// tabs against each other.</para>
///
/// <para><b>Authorisation gate.</b> Same workspace-membership check as
/// <c>GetProjectAgentPermissionsQuery</c> — missing/soft-deleted/non-member
/// caller all collapse to the
/// <see cref="RemoveProjectAgentPermissionsHandler.NotFoundPrefix"/> sentinel
/// so the controller can return 404 without leaking existence.</para>
/// </summary>
public sealed record RemoveProjectAgentPermissionsCommand(
    Guid ProjectId,
    string CallerUserId
) : ICommand<Result>;

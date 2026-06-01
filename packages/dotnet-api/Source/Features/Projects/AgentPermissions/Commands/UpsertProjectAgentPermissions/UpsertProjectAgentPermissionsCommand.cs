using Source.Features.Projects.AgentPermissions.Models;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.AgentPermissions.Commands.UpsertProjectAgentPermissions;

/// <summary>
/// Create-or-update mutation for a project's Agent SDK permission override row.
/// Idempotent — the handler inserts a new row if none exists, or updates the
/// existing one in place. Returns the persisted <see cref="ProjectAgentPermissionsDto"/>
/// so the caller can confirm exactly what was written.
///
/// <para><b>Validation contract</b> (see <see cref="UpsertProjectAgentPermissionsHandler"/>
/// for the implementation):</para>
/// <list type="bullet">
///   <item><see cref="PermissionMode"/> must be one of
///         <c>default | acceptEdits | bypassPermissions | plan | dontAsk</c>.
///         The SDK's <c>auto</c> mode is intentionally excluded per the spec's
///         Non-Goals.</item>
///   <item>If <see cref="PermissionMode"/> is <c>bypassPermissions</c>,
///         <see cref="AllowDangerouslySkipPermissions"/> must be <c>true</c>.
///         Surfacing this as a validation error gives the settings UI a clear
///         message to render instead of the SDK silently refusing to honour
///         bypass mode.</item>
/// </list>
///
/// <para><b>Authorisation gate.</b> Caller must be a member of the project's
/// workspace — same shape as <c>GetProjectHandler</c>. Missing/soft-deleted/
/// non-member all collapse to the <see cref="UpsertProjectAgentPermissionsHandler.NotFoundPrefix"/>
/// sentinel so the controller can map them to 404 without leaking existence.</para>
/// </summary>
public sealed record UpsertProjectAgentPermissionsCommand(
    Guid ProjectId,
    string CallerUserId,
    bool CallerIsSuperAdmin,
    string PermissionMode,
    bool AllowDangerouslySkipPermissions,
    IReadOnlyList<string> AllowedTools,
    IReadOnlyList<string> DisallowedTools,
    IReadOnlyList<string> AdditionalDirectories
) : ICommand<Result<ProjectAgentPermissionsDto>>;

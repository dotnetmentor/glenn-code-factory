namespace Source.Features.Projects.AgentPermissions.Models;

/// <summary>
/// Wire shape for a project's Agent SDK permission override row. Mirrors the
/// fields on <see cref="Source.Features.Projects.Models.ProjectAgentPermissions"/>
/// plus the owning <see cref="ProjectId"/> so the frontend can hydrate /
/// round-trip the row without an extra lookup.
///
/// <para><b>Lifecycle reminder.</b> Presence of this DTO in a
/// <c>GET</c> response means the project has an override; the
/// <c>GET</c> endpoint returns <c>null</c> (not 404) when no override exists,
/// which is the "fall through to system defaults" signal the settings UI uses
/// to render its toggle in the "off" position.</para>
///
/// <list type="bullet">
///   <item><see cref="PermissionMode"/> is one of
///         <c>default | acceptEdits | bypassPermissions | plan | dontAsk</c>.
///         The validation lives in the command handler, but the wire shape
///         keeps the value as a free-form string to match the entity column
///         and the SDK's canonical vocabulary.</item>
///   <item>The three list fields are always present (never null); empty list
///         means "no entries", distinct from the absent-row case which the
///         resolver treats as "fall back to system defaults".</item>
/// </list>
/// </summary>
public sealed record ProjectAgentPermissionsDto(
    Guid ProjectId,
    string PermissionMode,
    bool AllowDangerouslySkipPermissions,
    IReadOnlyList<string> AllowedTools,
    IReadOnlyList<string> DisallowedTools,
    IReadOnlyList<string> AdditionalDirectories
);

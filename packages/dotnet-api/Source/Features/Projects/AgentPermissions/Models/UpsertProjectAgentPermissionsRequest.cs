namespace Source.Features.Projects.AgentPermissions.Models;

/// <summary>
/// Request body for <c>PUT /api/projects/{projectId}/agent-permissions</c>.
/// Carries the five SDK-shaped fields the caller wants to write to the
/// project's override row. Idempotent / upsert semantics — the handler
/// inserts a new row if none exists, or updates the existing one in place.
///
/// <para><b>Validation contract (enforced in the command handler):</b></para>
/// <list type="bullet">
///   <item><see cref="PermissionMode"/> must be one of
///         <c>default | acceptEdits | bypassPermissions | plan | dontAsk</c>.
///         The SDK's <c>auto</c> mode is rejected per the spec's Non-Goals.</item>
///   <item>If <see cref="PermissionMode"/> is <c>bypassPermissions</c>,
///         <see cref="AllowDangerouslySkipPermissions"/> must be <c>true</c> —
///         the SDK requires the explicit opt-in to honour bypass mode.</item>
///   <item>The three list fields default to empty rather than null so a
///         caller who omits one ends up with an empty array (not a NRE).</item>
/// </list>
/// </summary>
public sealed record UpsertProjectAgentPermissionsRequest(
    string PermissionMode,
    bool AllowDangerouslySkipPermissions,
    IReadOnlyList<string>? AllowedTools = null,
    IReadOnlyList<string>? DisallowedTools = null,
    IReadOnlyList<string>? AdditionalDirectories = null
);

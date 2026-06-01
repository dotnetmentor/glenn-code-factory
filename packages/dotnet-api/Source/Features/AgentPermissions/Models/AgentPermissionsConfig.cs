using Tapper;

namespace Source.Features.AgentPermissions.Models;

/// <summary>
/// Effective Agent SDK permission configuration for a single project, as resolved
/// by <see cref="Services.IAgentPermissionsResolver"/>. Maps 1:1 to the
/// options the daemon may apply when wiring Cursor SDK turns.
///
/// <para><b>Resolution semantics — complete override, no merging.</b> If the
/// project has a <c>ProjectAgentPermissions</c> row, the values on that row are
/// returned verbatim. Otherwise the values come from the system catalog under
/// the <c>AgentPermissions</c> category. The two sources are never blended — see
/// the agent-sdk-permissions spec ("Non-Goals: We will not support merging").</para>
///
/// <para><b>Wire shape.</b> This record also flows over SignalR RPC from the
/// server back to the daemon (per the <c>GetEffectiveAgentPermissions</c>
/// runtime-hub method on the next card), so it carries the
/// <see cref="TranspilationSourceAttribute"/> marker that picks it up for
/// Tapper's TypeScript codegen. Keep the field names and shapes stable.</para>
///
/// <list type="bullet">
///   <item><see cref="PermissionMode"/> is a string (not an enum) because the
///         SDK's mode set is what owns the canonical vocabulary — encoding it
///         in C# would force a redeploy every time the SDK adds a mode.
///         Validation of the value lives in the command handler that writes
///         the project override row, not here.</item>
///   <item><see cref="AllowedTools"/> / <see cref="DisallowedTools"/> /
///         <see cref="AdditionalDirectories"/> are <c>IReadOnlyList&lt;string&gt;</c>
///         to make it obvious downstream code shouldn't mutate them — the
///         resolver hands out an immutable snapshot.</item>
/// </list>
/// </summary>
[TranspilationSource]
public sealed record AgentPermissionsConfig(
    string PermissionMode,
    bool AllowDangerouslySkipPermissions,
    IReadOnlyList<string> AllowedTools,
    IReadOnlyList<string> DisallowedTools,
    IReadOnlyList<string> AdditionalDirectories);

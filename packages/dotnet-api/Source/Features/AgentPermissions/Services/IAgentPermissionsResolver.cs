using Source.Features.AgentPermissions.Models;

namespace Source.Features.AgentPermissions.Services;

/// <summary>
/// Resolves the effective <see cref="AgentPermissionsConfig"/> for a project on
/// a per-turn basis. Called by the daemon-RPC handler that answers
/// <c>GetEffectiveAgentPermissions(projectId)</c> right before the daemon hands
/// its options when resolving project agent permissions for Cursor SDK turns.
///
/// <para><b>Contract — complete override, no merging.</b> If a
/// <c>ProjectAgentPermissions</c> row exists for the project, the row's fields
/// are returned verbatim. Otherwise the implementation reads the five
/// <c>AgentPermissions:*</c> keys from the system-settings catalog (falling
/// back to <c>AgentPermissionsDefaults</c> if a key isn't seeded). The two
/// sources are never blended — see the agent-sdk-permissions spec.</para>
///
/// <para><b>Lifetime.</b> Scoped (it touches the scoped
/// <c>ApplicationDbContext</c>). One resolution per turn is the expected call
/// pattern, so caching past the request boundary would be premature.</para>
/// </summary>
public interface IAgentPermissionsResolver
{
    /// <summary>
    /// Returns the effective permission config for <paramref name="projectId"/>.
    /// Never throws for "no project override" — that's the fall-through path.
    /// </summary>
    Task<AgentPermissionsConfig> ResolveForProjectAsync(Guid projectId, CancellationToken ct = default);
}

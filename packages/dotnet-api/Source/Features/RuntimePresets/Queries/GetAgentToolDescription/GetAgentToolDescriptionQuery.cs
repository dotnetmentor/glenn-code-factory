using Source.Features.RuntimePresets.Dtos;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimePresets.Queries.GetAgentToolDescription;

/// <summary>
/// Build the dynamic description + JSON schema the daemon embeds in the
/// agent's <c>propose_runtime_spec</c> tool definition. Enumerates the live
/// preset registry (via the global query filter, so soft-deleted presets are
/// invisible to the agent) and emits a <c>oneOf</c>-on-<c>kind</c> shape so
/// unknown slugs are rejected at the JSON-schema validator before the
/// proposal ever reaches the backend.
///
/// <para><b>Unauthenticated route.</b> The daemon fetches this at startup,
/// before any agent-side auth context exists; the controller exposes
/// <c>[AllowAnonymous]</c> for this reason. The response leaks no sensitive
/// data — it's just preset slugs + parameter keys, all of which are visible
/// on the public marketing site anyway.</para>
/// </summary>
public sealed record GetAgentToolDescriptionQuery : IQuery<Result<AgentToolDescriptionResponse>>;

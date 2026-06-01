namespace Source.Features.Mcp.Models;

/// <summary>
/// Wire shape returned by
/// <see cref="Source.Features.Mcp.Controllers.BootstrapMcpConfigController.Get"/>:
/// the list of MCP servers a freshly respawned daemon should wire into the Claude
/// SDK on cold-boot. Direct sibling of
/// <see cref="Source.Features.ProjectSecrets.Models.BootstrapEnvResponse"/> — same
/// daemon-facing bootstrap call shape, different payload (MCP catalog instead of
/// env vars).
///
/// <para><b>Empty list = empty bundle, not an error.</b> A control plane with no
/// enabled MCPs returns 200 with <see cref="Servers"/> = []. The daemon treats
/// that as "no MCPs to register" rather than an error condition.</para>
///
/// <para><b>Why a controller DTO (no <c>[TranspilationSource]</c>).</b> The
/// frontend does not consume this — only the daemon does, via a raw HTTP fetch
/// on boot. The DTO still flows through Swagger/Orval (because the controller
/// declares it via <c>[ProducesResponseType]</c>); we deliberately don't add
/// Tapper to the type for the same reason
/// <see cref="Source.Features.ProjectSecrets.Models.BootstrapEnvResponse"/> doesn't:
/// keep the transpiled-models surface to entities that are first-class in the
/// React layer.</para>
/// </summary>
public record BootstrapMcpConfigResponse(List<McpEntry> Servers);

/// <summary>
/// One MCP server entry in the bootstrap bundle. <see cref="Name"/> is the
/// catalog routing key (e.g. <c>"kanban"</c>), <see cref="Version"/> is the
/// free-form version label (e.g. <c>"v1"</c>), and <see cref="BaseUrl"/> is the
/// fully-qualified HTTP endpoint the daemon should hand to the Cursor SDK MCP client.
///
/// <para><b>BaseUrl is derived, not stored.</b> The control plane composes
/// <see cref="BaseUrl"/> at request time from the inbound request's scheme +
/// host plus the canonical <c>/api/mcp/{name}/{version}</c> path (the <c>/api/</c>
/// prefix is mandatory: the production Cloudflare tunnel forwards only
/// <c>/api/*</c> upstream, see KanbanMcpController route doc). This keeps
/// deployment-topology changes (Fly app rename, cloudflare tunnel host rotation)
/// from requiring a data migration. Mirrors the rationale recorded on
/// <see cref="McpServer"/> ("BaseUrl is intentionally NOT stored").</para>
/// </summary>
public record McpEntry(string Name, string Version, string BaseUrl);

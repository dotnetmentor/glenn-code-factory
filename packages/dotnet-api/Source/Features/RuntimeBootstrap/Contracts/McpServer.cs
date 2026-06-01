using Tapper;

namespace Source.Features.RuntimeBootstrap.Contracts;

/// <summary>
/// One MCP (Model Context Protocol) server entry destined for
/// <c>/data/.glenn/mcp.json</c>. The daemon hands this list to the agent SDK so
/// Claude can call platform MCP tools (kanban, planning, file storage, etc.).
///
/// <para><see cref="Scope"/> is an opaque token signed by main API that the MCP
/// server validates on every call — exact semantics are owned by the
/// <c>mcp-scoping</c> spec; we just transport the value here.</para>
/// </summary>
[TranspilationSource]
public record McpServer(string Name, string Url, string Scope);

namespace Source.Features.Mcp.Framework;

/// <summary>
/// Marks a controller class as an MCP server endpoint and supplies the
/// <see cref="Name"/> and <see cref="Version"/> the framework records on every
/// audit row. Read by <see cref="McpControllerBase"/> via reflection (with a
/// per-type cache) on first invocation of a derived controller.
///
/// <para><b>Why an attribute and not a virtual property.</b> The name is a
/// stable contract — it ends up in the <see cref="Source.Features.Mcp.Models.McpCall.ServerName"/>
/// audit column, in dashboards, and in rate-limit configuration keys. An
/// attribute keeps it declaratively obvious at the top of the controller and
/// makes "missing wire-up" a compile-time concern surfaced loudly at boot.</para>
///
/// <para>Mirrors the convention used by ASP.NET Core's own <c>[ApiController]</c> /
/// <c>[Route]</c> — the routing key is metadata on the type, never a runtime value.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class McpServerAttribute : Attribute
{
    /// <summary>
    /// Routing key for the MCP (e.g. <c>"kanban"</c>). Recorded verbatim into the
    /// <see cref="Source.Features.Mcp.Models.McpCall.ServerName"/> audit column.
    /// Must match the <see cref="Source.Features.Mcp.Models.McpServer.Name"/>
    /// catalog row when one exists.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Free-form version label exposed in the framework's capability handshake
    /// (e.g. <c>"v1"</c>). Maintainer bumps this when the MCP's tool surface changes.
    /// </summary>
    public string Version { get; }

    public McpServerAttribute(string name, string version)
    {
        Name = name;
        Version = version;
    }
}

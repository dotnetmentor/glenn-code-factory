using Source.Shared;
using Source.Shared.Events;

namespace Source.Features.Mcp.Models;

/// <summary>
/// Catalog row for an MCP server registered with the framework. One row per logical
/// MCP — e.g. <c>"kanban"</c>, <c>"errors"</c>, <c>"docs"</c>. The framework looks
/// up enablement / version / default-on state by <see cref="Name"/> at dispatch time.
///
/// <list type="bullet">
///   <item><see cref="Name"/> is the routing key the daemon and clients use to
///         address the MCP. Capped at 100 chars and unique among non-soft-deleted
///         rows (partial unique index, mirroring the
///         <see cref="Source.Features.ProjectSecrets.Models.ProjectSecret"/>
///         <c>(ProjectId, Key)</c> pattern) — deleted rows stay for audit / re-add.</item>
///   <item><see cref="Version"/> is a free-form semver-ish string the framework
///         exposes in its capability handshake. Capped at 20 chars. Bumped by the
///         maintainer when the MCP's tool surface changes.</item>
///   <item><see cref="DefaultEnabled"/> controls the initial per-project enablement
///         when a project first encounters this MCP. The per-project toggle lives
///         in a separate table (deferred until multiple MCPs ship).</item>
///   <item>Soft-deletable so future per-project enablement rows referencing this
///         server stay coherent across a yank-then-re-register cycle.</item>
///   <item><b>BaseUrl is intentionally NOT stored</b> — it's derived at runtime
///         from the request host, so changing deployment topology never requires
///         a data migration.</item>
/// </list>
///
/// <para>Inherits <see cref="Entity"/> so future cards can raise events from
/// instance methods without a model change.</para>
/// </summary>
public class McpServer : Entity, IAuditable, ISoftDelete
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Routing key for the MCP (e.g. <c>"kanban"</c>). Capped at 100 chars.
    /// Unique among non-soft-deleted rows via a partial index.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Free-form version label the framework exposes in its capability handshake
    /// (e.g. <c>"v1"</c>). Capped at 20 chars.
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Initial per-project enablement state when a project first sees this MCP.
    /// Defaults to true. The per-project toggle row lives in a separate table.
    /// </summary>
    public bool DefaultEnabled { get; set; } = true;

    // -------- IAuditable --------
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // -------- ISoftDelete --------
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
}

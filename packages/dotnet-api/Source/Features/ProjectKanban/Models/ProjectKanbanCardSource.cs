using Tapper;

namespace Source.Features.ProjectKanban.Models;

/// <summary>
/// Provenance discriminator on a <see cref="ProjectKanbanCard"/>: was the row
/// opened by a human via the REST surface, or by Claude via the kanban MCP?
/// Set at creation, immutable thereafter (the spec deliberately scopes
/// provenance to "where did this come from", not "who last edited it" — the
/// per-event audit trail in <c>StoredDomainEvents</c> already covers updates).
///
/// <para>Stable ordinals — persisted as <c>int</c> on the entity and
/// transpiled to TypeScript by Tapper for the frontend badge. DO NOT reorder.</para>
/// </summary>
[TranspilationSource]
public enum ProjectKanbanCardSource
{
    /// <summary>UI user opened the card via the REST controller. Default.</summary>
    Human = 0,

    /// <summary>Agent (daemon-side Claude) opened the card via the kanban MCP.</summary>
    Agent = 1,
}

using Tapper;

namespace Source.Features.ProjectKanban.Models;

/// <summary>
/// Priority bucket for a <see cref="ProjectKanbanCard"/>. Persisted as
/// <c>int</c> so reordering or adding new entries later doesn't shift existing rows.
///
/// <list type="bullet">
///   <item><see cref="Low"/> — nice-to-have; not blocking.</item>
///   <item><see cref="Medium"/> — default; ordinary work.</item>
///   <item><see cref="High"/> — important; should land soon.</item>
///   <item><see cref="Urgent"/> — drop everything else.</item>
/// </list>
///
/// <para>Default value for new cards is <see cref="Medium"/> — see
/// <see cref="ProjectKanbanCard.Create"/>.</para>
/// </summary>
[TranspilationSource]
public enum ProjectKanbanCardPriority
{
    Low = 0,
    Medium = 1,
    High = 2,
    Urgent = 3,
}

using Tapper;

namespace Source.Features.ProjectKanban.Models;

/// <summary>
/// Column / lifecycle bucket for a <see cref="ProjectKanbanCard"/>. Persisted as
/// <c>int</c> so reordering or adding new entries later doesn't shift existing rows.
///
/// <list type="bullet">
///   <item><see cref="Backlog"/> — captured but not yet committed to the active board.</item>
///   <item><see cref="Todo"/> — committed; ready to be picked up.</item>
///   <item><see cref="InProgress"/> — actively being worked on.</item>
///   <item><see cref="Done"/> — completed.</item>
/// </list>
/// </summary>
[TranspilationSource]
public enum ProjectKanbanCardStatus
{
    Backlog = 0,
    Todo = 1,
    InProgress = 2,
    Done = 3,
}

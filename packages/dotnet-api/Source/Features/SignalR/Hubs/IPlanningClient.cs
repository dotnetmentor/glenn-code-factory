using Source.Features.SignalR.Contracts;
using TypedSignalR.Client;

namespace Source.Features.SignalR.Hubs;

/// <summary>
/// Strongly-typed receiver interface for clients connected to <see cref="PlanningHub"/>.
/// Every method here is invoked by the server on the React client; the React side
/// registers handlers for these names. Keeping the contract on a single interface
/// means a backend change that drops a method breaks compile in the generated
/// TypeScript — same convention as <see cref="IAgentClient"/>.
///
/// <para>Payloads are intentionally payload-light. The frontend re-fetches via
/// Orval after invalidation; the notification's job is "tell me something changed
/// and what to refetch", not "transport the new state". Keeps the wire small
/// and avoids two sources of truth for entity shape.</para>
/// </summary>
[Receiver]
public interface IPlanningClient
{
    /// <summary>
    /// Pushed when a <c>Specification</c> is created / updated / deleted under
    /// a project the connection is subscribed to (via
    /// <c>PlanningHub.JoinProject</c>). Drives the live spec list / detail
    /// without polling.
    /// </summary>
    Task SpecificationChanged(SpecificationChangedNotification payload);

    /// <summary>
    /// Pushed for every lifecycle transition on a <c>ProjectKanbanCard</c> —
    /// create, edit, move (status / position), soft-delete. Drives the live
    /// kanban board re-render in every open tab on the project.
    /// </summary>
    Task CardChanged(CardChangedNotification payload);

    /// <summary>
    /// Pushed for every lifecycle transition on a <c>ProjectKanbanCardSubtask</c> —
    /// create, toggle (complete / incomplete), soft-delete. Routed by the
    /// parent card's <c>ProjectId</c> so subscribers join one group per
    /// project, not per card.
    /// </summary>
    Task SubtaskChanged(SubtaskChangedNotification payload);
}

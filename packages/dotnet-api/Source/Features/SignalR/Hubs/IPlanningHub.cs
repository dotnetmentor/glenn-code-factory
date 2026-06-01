using TypedSignalR.Client;

namespace Source.Features.SignalR.Hubs;

/// <summary>
/// Strongly-typed server method surface for <see cref="PlanningHub"/> — the
/// methods React clients are allowed to invoke. The matching
/// <see cref="IPlanningClient"/> receiver interface is the server-to-React push
/// channel. Two interfaces, two directions, one hub — same convention as
/// <see cref="IAgentHub"/> + <see cref="IAgentClient"/>.
///
/// <para>The hub is subscribe-only on the inbound side: a tab joins / leaves
/// the projects it cares about. There is no <c>SubmitX</c> entry point on this
/// hub — domain mutations happen through the REST controllers (Orval mutations
/// on the client), and the resulting domain events fan out via
/// <see cref="IPlanningClient"/> push methods.</para>
///
/// <para>Lifecycle hooks (<c>OnConnectedAsync</c>, <c>OnDisconnectedAsync</c>)
/// are SignalR-framework concerns and intentionally not declared here.</para>
/// </summary>
[Hub]
public interface IPlanningHub
{
    /// <summary>
    /// Subscribe this connection to the <c>project:{projectId}</c> SignalR
    /// group so the client receives every planning broadcast
    /// (<see cref="IPlanningClient.SpecificationChanged"/>,
    /// <see cref="IPlanningClient.CardChanged"/>,
    /// <see cref="IPlanningClient.SubtaskChanged"/>) for that project.
    /// Called by the frontend on project board / spec list mount. Idempotent —
    /// re-joining a group is a SignalR no-op.
    /// </summary>
    Task JoinProject(Guid projectId);

    /// <summary>
    /// Symmetric counterpart to <see cref="JoinProject"/>: removes this
    /// connection from the <c>project:{projectId}</c> group when the planning
    /// surface unmounts. Idempotent — SignalR's <c>RemoveFromGroupAsync</c>
    /// is a no-op for non-members.
    /// </summary>
    Task LeaveProject(Guid projectId);
}

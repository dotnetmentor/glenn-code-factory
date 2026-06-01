using Source.Features.SignalR.Contracts;
using TypedSignalR.Client;

namespace Source.Features.SignalR.Hubs;

/// <summary>
/// Strongly-typed server method surface for <see cref="AgentHub"/> — the methods
/// React clients are allowed to invoke. Decorated with <c>[Hub]</c> so the
/// TypedSignalR.Client.TypeScript analyzer emits a real
/// <c>HubProxyFactoryProvider</c> entry; without it the generated index.ts ships
/// an empty stub and the React side gets <c>undefined</c> when it asks for a
/// proxy.
///
/// <para>The matching <see cref="IAgentClient"/> receiver interface is the
/// server-to-React push channel. Two interfaces, two directions, one hub.</para>
///
/// <para>Lifecycle hooks (<c>OnConnectedAsync</c>, <c>OnDisconnectedAsync</c>) are
/// SignalR-framework concerns and are not declared here.</para>
/// </summary>
[Hub]
public interface IAgentHub
{
    Task<SubmitPromptResponse> SubmitPrompt(SubmitPromptPayload payload);

    Task CancelTurn(CancelTurnRequest payload);

    Task<List<AgentEventNotification>> RequestEventReplay(EventReplayRequest payload);

    /// <summary>
    /// Subscribe this connection to the <c>workspace-{workspaceId}</c> SignalR
    /// group so the sidebar receives cross-project broadcasts (runtime state,
    /// turn lifecycle, conversation rename) for every project in the workspace,
    /// not just the one in the current tab. Returns once the membership check
    /// succeeds and the group join is committed; throws <c>HubException</c> if
    /// the caller is not a member of that workspace.
    /// </summary>
    Task JoinWorkspace(string workspaceId);

    /// <summary>
    /// Symmetric counterpart to <see cref="JoinWorkspace"/>: removes this
    /// connection from the <c>workspace-{workspaceId}</c> group. Idempotent —
    /// safe to call even if the connection isn't currently a member.
    /// </summary>
    Task LeaveWorkspace(string workspaceId);

    /// <summary>
    /// React-to-server invocation: the user clicked one of the four actions on
    /// an inline approval card and the daemon's pending <c>canUseTool</c>
    /// callback needs the answer. The hub resolves
    /// project → active runtime → daemon connection and forwards via
    /// <see cref="IRuntimeClient.PermissionResolved"/>, correlated by
    /// <c>ToolUseId</c>. Pure relay, no persistence — session-scoped wire only.
    /// </summary>
    Task ResolvePermission(ResolvePermissionPayload payload);

    /// <summary>
    /// Subscribe this connection to the <c>runtime-events:{runtimeId}</c>
    /// SignalR group so the runtime drawer's Timeline tab receives live
    /// pushes for every event the daemon emits. The hub verifies the caller
    /// has access to the runtime (the runtime row must exist and resolve to
    /// a project the user can reach — see implementation for the current
    /// gate). Idempotent — re-joining a group is a SignalR no-op.
    /// </summary>
    Task SubscribeToRuntimeEvents(Guid runtimeId);

    /// <summary>
    /// Symmetric counterpart to <see cref="SubscribeToRuntimeEvents"/>:
    /// removes this connection from the <c>runtime-events:{runtimeId}</c>
    /// group when the drawer's Timeline tab closes. Idempotent — SignalR's
    /// <c>RemoveFromGroupAsync</c> is a no-op for non-members and no auth
    /// check is performed (leaving a group you may already have left is
    /// harmless).
    /// </summary>
    Task UnsubscribeFromRuntimeEvents(Guid runtimeId);

    /// <summary>
    /// Subscribe this connection to the <c>service-logs:{runtimeId}:{serviceName}</c>
    /// SignalR group so the runtime drawer's Logs tab receives every line
    /// the daemon's <c>tail -F</c> reads off the supervised service's log
    /// file. The hub validates the caller has access to the runtime AND
    /// the supplied service name is declared in the current
    /// <c>ProjectRuntime.Spec</c> — undeclared service names are refused
    /// silently so a curious frontend can't drive the daemon to spawn
    /// arbitrary tail processes. The first subscriber on a given
    /// (runtimeId, serviceName) pair causes the hub to push
    /// <see cref="IRuntimeClient.StartLogTail"/> to the daemon; subsequent
    /// subscribers ride on the daemon's LogTailer reference counter
    /// without a second push.
    /// </summary>
    Task SubscribeToServiceLogs(Guid runtimeId, string serviceName);

    /// <summary>
    /// Symmetric counterpart to <see cref="SubscribeToServiceLogs"/>:
    /// removes this connection from the
    /// <c>service-logs:{runtimeId}:{serviceName}</c> group when the Logs
    /// tab closes. The hub pushes <see cref="IRuntimeClient.StopLogTail"/>
    /// to the daemon on every call — the daemon's reference counter
    /// debounces it down to a single SIGTERM when the last subscriber
    /// detaches. Idempotent and unauthenticated (leaving a group you may
    /// already have left is harmless).
    /// </summary>
    Task UnsubscribeFromServiceLogs(Guid runtimeId, string serviceName);

    /// <summary>
    /// runtime-observability-super-admin — super-admin only. Subscribe this
    /// connection to the <c>daemon-logs:{runtimeId}</c> SignalR group so the
    /// runtime drawer's Daemon Logs tab receives every line the daemon's
    /// <c>tail -F</c> reads off
    /// <c>/var/log/supervisor/agent.out.log</c> + <c>agent.err.log</c>. The
    /// hub gates the call on the global super-admin claim (the daemon's own
    /// stdout/stderr can carry operator-sensitive bootstrap detail) and pushes
    /// <see cref="IRuntimeClient.StartDaemonLogTail"/> to the daemon on the
    /// first subscriber. Subsequent subscribers ride on the daemon's
    /// reference counter without a second push.
    /// </summary>
    Task SubscribeToDaemonLogs(Guid runtimeId);

    /// <summary>
    /// Symmetric counterpart to <see cref="SubscribeToDaemonLogs"/>: removes
    /// this connection from the <c>daemon-logs:{runtimeId}</c> group when the
    /// drawer's Daemon Logs tab closes. The hub pushes
    /// <see cref="IRuntimeClient.StopDaemonLogTail"/> to the daemon on every
    /// call — the daemon's reference counter debounces it down to a single
    /// SIGTERM when the last subscriber detaches. Idempotent and
    /// unauthenticated (leaving a group you may already have left is
    /// harmless).
    /// </summary>
    Task UnsubscribeFromDaemonLogs(Guid runtimeId);
}

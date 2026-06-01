using System.Collections.Concurrent;
using Source.Features.SignalR.Events;
using Source.Shared.Events;

namespace Source.Features.SignalR.Services;

/// <summary>
/// In-memory map of <c>runtimeId → connectionId</c> for the daemon's current
/// SignalR connection.
///
/// <para><b>Why this exists.</b> ASP.NET Core SignalR's
/// <c>IHubContext&lt;THub, TClient&gt;.Clients.Group(...).SomeMethodWithReturn(...)</c>
/// throws <c>System.InvalidOperationException: InvokeAsync only works with Single
/// clients.</c> at runtime — typed-client invocations that return a value compile
/// down to <c>InvokeAsync</c>, which the framework only supports on a single
/// client connection (<c>Clients.Client(connectionId)</c>), never on a group or
/// "all clients" target. Fire-and-forget (<c>Task</c>-returning) typed methods
/// like <see cref="IRuntimeClient.MergeBranch"/> work fine on a group; methods
/// like <see cref="IRuntimeClient.GetChangedFiles"/> /
/// <see cref="IRuntimeClient.GetFileDiff"/> that await a daemon response do not.
/// This registry hands the controller the connection id it needs to call those
/// "ask the daemon, await its reply" methods.</para>
///
/// <para><b>Lifetime.</b> Singleton, process-local. There is no SignalR
/// backplane (single API process; see <c>AddRealTimeServices</c>), so a plain
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> is correct. If a Redis /
/// Azure SignalR backplane is ever introduced, this needs to move behind the
/// backplane (or every API replica needs its own copy fed by a shared event
/// stream); see the disconnect race note below.</para>
///
/// <para><b>Reconnect ordering.</b> A daemon that drops and reconnects will
/// fire <c>OnConnectedAsync</c> on the new connection BEFORE
/// <c>OnDisconnectedAsync</c> resolves on the old one (transport timeouts can
/// race). The registry must always reflect the most-recent connect, so
/// <see cref="Set"/> overwrites unconditionally and <see cref="Remove"/> is a
/// compare-and-delete that only removes the entry when the connection id still
/// matches — a stale "old connection died" event after the new connection
/// already wrote its id will be a no-op.</para>
/// </summary>
public interface IRuntimeConnectionRegistry
{
    /// <summary>
    /// Record the connection id for the given runtime. Overwrites any prior
    /// entry — see the reconnect-ordering note in the interface remarks.
    /// </summary>
    void Set(Guid runtimeId, string connectionId);

    /// <summary>
    /// Compare-and-delete: only removes the entry when the recorded
    /// connection id matches <paramref name="connectionId"/>. A stale
    /// disconnect event for an already-replaced connection is a no-op.
    /// </summary>
    void Remove(Guid runtimeId, string connectionId);

    /// <summary>
    /// Returns the current connection id for the runtime, or <c>null</c> if
    /// no daemon is connected. Callers should treat <c>null</c> as 503
    /// "daemon offline" — same UX as the <c>IOException</c> path that fires
    /// when SignalR's transport is up but the group is empty.
    /// </summary>
    string? TryGet(Guid runtimeId);
}

public sealed class RuntimeConnectionRegistry : IRuntimeConnectionRegistry
{
    private readonly ConcurrentDictionary<Guid, string> _byRuntime = new();

    public void Set(Guid runtimeId, string connectionId)
        => _byRuntime[runtimeId] = connectionId;

    public void Remove(Guid runtimeId, string connectionId)
    {
        // Compare-and-delete: TryRemove(KeyValuePair) only removes when both
        // key AND value match what's currently in the dict. This is the only
        // way to make "remove only if I'm still the current connection"
        // race-safe against an interleaved reconnect that wrote a newer id.
        _byRuntime.TryRemove(new KeyValuePair<Guid, string>(runtimeId, connectionId));
    }

    public string? TryGet(Guid runtimeId)
        => _byRuntime.TryGetValue(runtimeId, out var id) ? id : null;
}

/// <summary>
/// Populates the registry on connect. Subscribed via the standard
/// <see cref="IEventHandler{TEvent}"/> dispatch path (the hub publishes
/// <see cref="RuntimeConnected"/> after it has joined the group + bootstrapped
/// config), which keeps the hub itself unaware of this consumer.
/// </summary>
public class TrackRuntimeConnectionHandler : IEventHandler<RuntimeConnected>
{
    private readonly IRuntimeConnectionRegistry _registry;

    public TrackRuntimeConnectionHandler(IRuntimeConnectionRegistry registry)
    {
        _registry = registry;
    }

    public Task Handle(RuntimeConnected notification, CancellationToken cancellationToken)
    {
        _registry.Set(notification.RuntimeId, notification.ConnectionId);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Drops the registry entry on disconnect using the compare-and-delete contract
/// of <see cref="IRuntimeConnectionRegistry.Remove"/> — see the reconnect-race
/// note in the interface remarks for why a plain <c>Remove(runtimeId)</c> would
/// be wrong.
/// </summary>
public class UntrackRuntimeConnectionHandler : IEventHandler<RuntimeDisconnected>
{
    private readonly IRuntimeConnectionRegistry _registry;

    public UntrackRuntimeConnectionHandler(IRuntimeConnectionRegistry registry)
    {
        _registry = registry;
    }

    public Task Handle(RuntimeDisconnected notification, CancellationToken cancellationToken)
    {
        _registry.Remove(notification.RuntimeId, notification.ConnectionId);
        return Task.CompletedTask;
    }
}

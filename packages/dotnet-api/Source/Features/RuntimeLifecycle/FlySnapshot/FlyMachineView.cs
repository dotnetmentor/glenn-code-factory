namespace Source.Features.RuntimeLifecycle.FlySnapshot;

/// <summary>
/// The "what Fly.io says" half of the <see cref="FlySnapshotResponse"/>. A trimmed
/// projection of <see cref="Source.Features.FlyManagement.Models.FlyMachine"/> so the
/// frontend doesn't need to know the upstream's full schema.
///
/// <para>The card spec mentioned <c>Image</c>, <c>UpdatedAt</c>, and an <c>Events</c>
/// array, but the project's <c>FlyMachine</c> record only surfaces the fields below
/// (we deliberately kept it minimal — see <c>FlyMachine.cs</c>). When we extend the
/// upstream model to expose more (e.g. <c>Config.Image</c>) this view is the natural
/// place to add corresponding properties.</para>
/// </summary>
public sealed class FlyMachineView
{
    /// <summary>Fly machine id (e.g. <c>"148ed397c12483"</c>).</summary>
    public required string Id { get; init; }

    /// <summary>
    /// Free-form name Fly carries on the machine. For project runtimes this is the
    /// <c>"rt_{guid}"</c> pattern produced by the provisioner; for control-plane
    /// machinery it varies.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Live Fly machine state (<c>"started"</c>, <c>"stopped"</c>, <c>"suspended"</c>,
    /// <c>"destroyed"</c>, …). Stringly-typed to match the upstream — Fly keeps adding
    /// new states.
    /// </summary>
    public required string State { get; init; }

    /// <summary>Fly region the machine is currently running in.</summary>
    public required string Region { get; init; }

    /// <summary>
    /// Fly instance id for the running VM. Changes every time the machine is restarted —
    /// useful for distinguishing a "same machine, fresh boot" from a stale earlier life.
    /// Null when the machine has never booted (still in <c>created</c>).
    /// </summary>
    public string? InstanceId { get; init; }

    /// <summary>Fly-internal 6PN address for the machine. Null on never-booted machines.</summary>
    public string? PrivateIp { get; init; }

    /// <summary>UTC timestamp Fly recorded for machine creation.</summary>
    public DateTime CreatedAt { get; init; }
}

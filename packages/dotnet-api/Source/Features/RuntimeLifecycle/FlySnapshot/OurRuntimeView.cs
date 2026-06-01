namespace Source.Features.RuntimeLifecycle.FlySnapshot;

/// <summary>
/// The "what our database thinks" half of the
/// <see cref="FlySnapshotResponse"/>. A flat projection of the fields on
/// <see cref="Models.ProjectRuntime"/> that an operator needs to do a side-by-side
/// reality-check against Fly's live machine view.
///
/// <para><see cref="State"/> is serialised as a string (not the enum) so the frontend
/// gets the human-readable value (<c>"Online"</c>, <c>"Suspended"</c>, …) directly,
/// matching how the drift response surfaces it.</para>
/// </summary>
public sealed class OurRuntimeView
{
    /// <summary>DB primary key of the runtime.</summary>
    public Guid RuntimeId { get; init; }

    /// <summary>Owning project's id.</summary>
    public Guid ProjectId { get; init; }

    /// <summary>Lifecycle state as a string — see <see cref="Models.RuntimeState"/>.</summary>
    public required string State { get; init; }

    /// <summary>Fly region the runtime is pinned to, e.g. <c>"arn"</c>.</summary>
    public required string Region { get; init; }

    /// <summary>Fly machine id, if the runtime has progressed past Pending. Null on never-provisioned rows.</summary>
    public string? FlyMachineId { get; init; }

    /// <summary>UTC timestamp of the last daemon heartbeat. Null until the runtime first boots.</summary>
    public DateTime? LastHeartbeatAt { get; init; }

    /// <summary>UTC timestamp of the last <see cref="Models.ProjectRuntime.State"/> change.</summary>
    public DateTime StateChangedAt { get; init; }

    /// <summary>UTC timestamp the row was created — the lifetime of this runtime's record.</summary>
    public DateTime CreatedAt { get; init; }
}

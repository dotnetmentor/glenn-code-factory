namespace Source.Features.FlyManagement.Models;

/// <summary>
/// Subset of the Fly volume resource (<c>GET /v1/apps/{app}/volumes/{id}</c>) we surface
/// to callers. As with <see cref="FlyMachine"/>, snake-case fields on the wire
/// (<c>size_gb</c>, <c>attached_machine_id</c>, <c>created_at</c>) are mapped via the
/// FlyClient's shared <c>JsonNamingPolicy.SnakeCaseLower</c> serialiser.
///
/// <para><see cref="State"/> is deliberately stringly-typed: Fly's volume lifecycle has
/// grown over time (<c>pending</c>, <c>created</c>, <c>ready</c>, <c>destroying</c>,
/// <c>destroyed</c>, <c>scheduling_destroy</c>, ...) and a closed enum here would just
/// break us on the next addition. <see cref="AttachedMachineId"/> is nullable because
/// fresh volumes are unattached and become attached only when a machine mounts them.</para>
/// </summary>
public record FlyVolume(
    string Id,
    string Name,
    string Region,
    int SizeGb,
    string State,
    string? AttachedMachineId,
    bool Encrypted,
    DateTime CreatedAt);

namespace Source.Features.FlyManagement.Models;

/// <summary>
/// Subset of the Fly machine resource (<c>GET /v1/apps/{app}/machines/{id}</c>) we
/// surface to callers. Snake-case fields on the wire are mapped via the FlyClient's
/// <c>JsonNamingPolicy.SnakeCaseLower</c> serialiser settings.
///
/// <para>State values are deliberately stringly-typed: Fly adds new states over time
/// (<c>created</c>, <c>starting</c>, <c>started</c>, <c>stopping</c>, <c>stopped</c>,
/// <c>suspended</c>, <c>destroying</c>, <c>destroyed</c>, <c>replacing</c>, <c>failed</c>),
/// and forcing a closed enum here would just break us on the next addition.</para>
/// </summary>
public record FlyMachine(
    string Id,
    string Name,
    string State,
    string Region,
    string? InstanceId,
    string? PrivateIp,
    DateTime CreatedAt);

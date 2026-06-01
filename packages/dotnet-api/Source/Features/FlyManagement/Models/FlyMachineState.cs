namespace Source.Features.FlyManagement.Models;

/// <summary>
/// Response shape of <c>GET /v1/apps/{app}/machines/{id}/wait?state={target}</c>.
/// Fly blocks the request until the machine reaches the requested state (or the
/// supplied timeout expires) and returns the observed state with the timestamp at
/// which it last transitioned.
///
/// <para>State is stringly-typed for the same reason as <see cref="FlyMachine.State"/>
/// — Fly adds new lifecycle values over time and a closed enum here would be a tax
/// on every future addition.</para>
/// </summary>
public record FlyMachineState(string State, DateTime UpdatedAt);

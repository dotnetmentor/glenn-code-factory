namespace Source.Features.FlyManagement.Models;

/// <summary>
/// Body of <c>POST /v1/apps/{app}/machines/{id}/stop</c>. Both fields are optional —
/// passing <c>null</c> to <see cref="FlyClient.StopMachineAsync"/> sends an empty body
/// and lets Fly use its own defaults (graceful <c>SIGINT</c>, then <c>SIGKILL</c> after
/// a built-in grace period).
///
/// <para><see cref="Signal"/> overrides which POSIX signal Fly delivers first
/// (<c>"SIGTERM"</c>, <c>"SIGKILL"</c>, ...). <see cref="Timeout"/> is the number of
/// seconds Fly waits before escalating to <c>SIGKILL</c> when the process refuses to
/// exit. Both surface as snake_case on the wire via the FlyClient's shared serialiser
/// settings, so the property names here map to <c>signal</c> / <c>timeout</c>.</para>
/// </summary>
public record StopMachineRequest(string? Signal = null, int? Timeout = null);

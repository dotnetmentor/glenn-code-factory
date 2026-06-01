using Tapper;

namespace Source.Features.ProjectSecrets.Models;

/// <summary>
/// One env-var change shipped to the daemon over SignalR. Rides on
/// <c>ConfigUpdatePayload.EnvVarsDelta</c> as part of the live secret-rotation
/// fan-out (spec project-secrets Card 4).
///
/// <list type="bullet">
///   <item><see cref="Key"/> — the env-var name (e.g. <c>STRIPE_API_KEY</c>),
///         already validated as <c>^[A-Z][A-Z0-9_]*$</c> at command time.</item>
///   <item><see cref="Value"/> — the plaintext value to write. <c>null</c>
///         means "delete this key" — the daemon must remove the line from
///         <c>/data/.glenn/env</c>. Non-null means "upsert with this value".</item>
/// </list>
///
/// <para><b>Plaintext on the wire.</b> The plaintext only travels over the
/// authenticated SignalR connection between the API and the daemon; both ends
/// terminate inside our infrastructure (Fly's private network for daemons,
/// Cloudflare-fronted TLS for browsers — but daemons don't go through the
/// browser path). The daemon writes the value into the runtime's env file
/// under restrictive permissions; nothing here ever flows back to a client.
/// Logging is handled in spec project-secrets Card 6 (redacted keys never
/// log values).</para>
///
/// <para><b>[TranspilationSource].</b> Crosses to the daemon's hand-mirrored
/// TS in <c>packages/daemon/src/signalr/types.ts</c>; same convention as the
/// other hub payload types in this slice.</para>
/// </summary>
[TranspilationSource]
public record EnvVarDelta(string Key, string? Value);

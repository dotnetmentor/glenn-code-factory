using Tapper;

namespace Source.Features.ProjectSecrets.Models;

/// <summary>
/// Discrete actions captured by the secrets audit trail. Persisted as <c>int</c>
/// so adding new entries later doesn't shift existing rows.
///
/// <list type="bullet">
///   <item><see cref="Created"/> — a new secret was written.</item>
///   <item><see cref="Updated"/> — an existing secret's ciphertext / version was rotated.</item>
///   <item><see cref="Deleted"/> — a secret was soft-deleted via the admin path.</item>
///   <item><see cref="Revealed"/> — an authorised principal decrypted the value.</item>
///   <item><see cref="ListAttempted"/> — the project's secret list was enumerated
///         (keys only — never values; logged for tenancy / over-broad access auditing).</item>
///   <item><see cref="CrossTenantDenied"/> — a request tried to read / write a
///         secret in a project the caller does not own; logged regardless of
///         whether the principal would otherwise have permission.</item>
///   <item><see cref="BootstrapDelivered"/> — the daemon picked up the project's
///         secret bundle as part of its bootstrap handshake (Card 4 wires this).</item>
/// </list>
/// </summary>
[TranspilationSource]
public enum SecretAuditAction
{
    Created = 0,
    Updated = 1,
    Deleted = 2,
    Revealed = 3,
    ListAttempted = 4,
    CrossTenantDenied = 5,
    BootstrapDelivered = 6,
}

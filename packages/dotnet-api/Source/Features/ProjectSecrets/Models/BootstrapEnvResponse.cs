namespace Source.Features.ProjectSecrets.Models;

/// <summary>
/// Wire shape returned by <see cref="Source.Features.ProjectSecrets.Controllers.BootstrapEnvController.GetBootstrapEnv"/>:
/// the full set of (decrypted) env-var entries the daemon should write to its
/// <c>/data/.glenn/env</c> file on cold-boot.
///
/// <para><b>Empty list = empty bundle, not an error.</b> A project with no
/// secrets returns 200 with <see cref="Entries"/> = []. The daemon treats that
/// as "wipe the env file" rather than an error condition.</para>
///
/// <para><b>Plaintext lifetime.</b> Each <see cref="EnvVarEntry.Value"/> is the
/// plaintext result of an AES-GCM decrypt — the same pragmatic fallback used by
/// <see cref="Source.Features.ProjectSecrets.EventHandlers.PushSecretToRuntimeHandler"/>:
/// once decrypted into a <see cref="string"/>, .NET's immutable string semantics
/// mean we cannot deterministically zero the buffer. The values live on the heap
/// until GC. We never log them, never echo into telemetry, never include them
/// in the audit row.</para>
///
/// <para><b>Why a controller DTO (no <c>[TranspilationSource]</c>).</b> The
/// frontend does not consume this — only the daemon does, via a raw HTTP fetch
/// in spec 14 Card 8. The DTO still flows through Swagger/Orval (because the
/// controller declares it via <c>[ProducesResponseType]</c>); we deliberately
/// don't add Tapper to the type for the same reason Card 3's controller DTOs
/// don't: keep the transpiled-models surface to entities that are first-class
/// in the React layer.</para>
/// </summary>
public record BootstrapEnvResponse(List<EnvVarEntry> Entries);

/// <summary>
/// One env-var line in the bootstrap bundle. <see cref="Key"/> is the env-var
/// name (mirrors <see cref="ProjectSecret.Key"/> validation: <c>^[A-Z][A-Z0-9_]*$</c>),
/// <see cref="Value"/> is the decrypted plaintext.
///
/// <para>This shape mirrors <see cref="EnvVarDelta"/> deliberately — same
/// (Key, Value) layout — but represents an upsert-only snapshot rather than a
/// change-set. There's no "delete" sentinel here because a missing key in the
/// bundle already means "not present".</para>
/// </summary>
public record EnvVarEntry(string Key, string Value);

using Tapper;

namespace Source.Features.RuntimeBootstrap.Contracts;

/// <summary>
/// One environment variable destined for <c>/data/.glenn/env</c>. The value is
/// <b>already decrypted</b> by main API before we hand it to the daemon — see the
/// <c>project-secrets</c> spec — so the daemon never holds the encryption key. The
/// daemon writes <see cref="Key"/>=<see cref="Value"/> lines atomically and the file
/// is mode 600 owned by <c>agent</c>.
/// </summary>
[TranspilationSource]
public record EnvVar(string Key, string Value);

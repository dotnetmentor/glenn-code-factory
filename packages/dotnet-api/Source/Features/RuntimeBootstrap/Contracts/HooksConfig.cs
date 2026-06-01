using Tapper;

namespace Source.Features.RuntimeBootstrap.Contracts;

/// <summary>
/// Daemon hook configuration written to <c>/data/.glenn/hooks.json</c>. The shape
/// is defined by the <c>daemon-hooks-runner</c> spec; for now this is an opaque
/// placeholder so the bootstrap payload schema is complete and forward-compatible.
/// When the hook runner ships, fields land here without breaking the payload version.
/// </summary>
[TranspilationSource]
public record HooksConfig
{
    // Intentionally empty until daemon-hooks-runner spec defines the shape.
}

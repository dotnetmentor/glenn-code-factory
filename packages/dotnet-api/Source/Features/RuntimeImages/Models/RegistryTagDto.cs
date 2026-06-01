namespace Source.Features.RuntimeImages.Models;

/// <summary>
/// One row in the <c>GET /api/admin/runtime-images/registry-tags</c> response. Each item
/// represents a tag the operator can choose to register-and-activate from the super-admin
/// UI. The shape is deliberately flat and primitive-typed so the auto-generated TypeScript
/// surface (Orval) is trivial.
///
/// <para><see cref="Digest"/> is the manifest digest (<c>sha256:...</c>) — pinning future
/// machine boots to a digest is what makes "yanked" stick. <see cref="SizeBytes"/> and
/// <see cref="PushedAt"/> are best-effort: SizeBytes reflects the manifest's image-config
/// size (close enough for a UI column), PushedAt comes from the config blob's <c>created</c>
/// label and may be null for older builds. <see cref="GitSha"/> is sourced from the OCI
/// label <c>org.opencontainers.image.revision</c>; null when the build didn't stamp it.</para>
/// </summary>
public sealed record RegistryTagDto(
    string Tag,
    string Digest,
    long? SizeBytes,
    DateTime? PushedAt,
    string? GitSha);

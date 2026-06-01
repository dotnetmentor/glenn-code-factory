namespace Source.Features.RuntimeImages.Services;

/// <summary>
/// What we extract from a single OCI v2 manifest fetch. The <see cref="Digest"/> is the
/// content-addressable manifest digest (read from the <c>Docker-Content-Digest</c> response
/// header), <see cref="SizeBytes"/> is the on-disk image config size as advertised by the
/// manifest's <c>config.size</c> field, and <see cref="Labels"/> are the image labels
/// surfaced by the config blob — we read <c>org.opencontainers.image.revision</c> from
/// here to materialise the git SHA without a separate lookup.
///
/// <para><see cref="PushedAt"/> comes from the config blob's <c>created</c> timestamp
/// (RFC3339). It's a build-time stamp, not a registry-side push timestamp — Fly's
/// registry doesn't expose the latter — but it's the closest we get and matches what
/// operators expect to see in a "when did this image land" column.</para>
/// </summary>
public sealed record RegistryManifestInfo(
    string Digest,
    long SizeBytes,
    DateTime? PushedAt,
    IReadOnlyDictionary<string, string> Labels);

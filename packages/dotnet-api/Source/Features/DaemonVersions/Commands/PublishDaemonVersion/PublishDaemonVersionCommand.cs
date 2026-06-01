using Source.Features.DaemonVersions.Models;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.DaemonVersions.Commands.PublishDaemonVersion;

/// <summary>
/// Publish a new daemon bundle. The handler:
/// <list type="number">
///   <item>Buffers <see cref="BundleStream"/> into memory (we need to compute
///         SHA-256 + upload, and most streams aren't seekable);</item>
///   <item>Computes SHA-256 of the bytes; if <see cref="PreComputedSha256"/>
///         is supplied, verifies the two match (defensive integrity check
///         from the publisher);</item>
///   <item>Auto-generates a <c>Version</c> = <c>{yyyy.MM.dd.HHmmss}</c> UTC;</item>
///   <item>Uploads the bundle via <c>IFileStorageService</c> under
///         <c>daemon-bundles/</c>;</item>
///   <item>In a single SaveChanges transaction: deactivates the previous
///         active row in <see cref="Channel"/> (if any), inserts the new row
///         with <c>IsActive=true</c>.</item>
/// </list>
/// Returns the new version + a publicly-resolvable download URL.
/// </summary>
public sealed record PublishDaemonVersionCommand(
    Stream BundleStream,
    string ContentDisposition,
    string Channel = "stable",
    string? Notes = null,
    string? PreComputedSha256 = null
) : ICommand<Result<PublishDaemonVersionResponse>>;

/// <summary>
/// Wire shape for a successful publish. Mirrors <see cref="DaemonVersionDto"/>
/// in terms of fields but is the explicit contract for the publish endpoint.
/// </summary>
public record PublishDaemonVersionResponse
{
    public required Guid Id { get; init; }
    public required string Version { get; init; }
    public required string Channel { get; init; }
    public required string DownloadUrl { get; init; }
    public required string Sha256 { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTime ReleasedAt { get; init; }
}

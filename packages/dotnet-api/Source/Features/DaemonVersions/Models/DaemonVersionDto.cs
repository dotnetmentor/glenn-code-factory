namespace Source.Features.DaemonVersions.Models;

/// <summary>
/// Wire shape returned by both <c>GET /api/daemon-versions/resolve</c> (used by
/// the runtime bootstrap script on cold-boot) and <c>GET /api/daemon-versions</c>
/// (admin listing). Carries everything the bootstrap script needs to pull and
/// verify the tarball.
/// </summary>
public record DaemonVersionDto
{
    public required Guid Id { get; init; }
    public required string Version { get; init; }
    public required string Channel { get; init; }
    public required string DownloadUrl { get; init; }
    public required string Sha256 { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTime ReleasedAt { get; init; }
    public required bool IsActive { get; init; }
    public string? Notes { get; init; }
    public string? GitSha { get; init; }
}

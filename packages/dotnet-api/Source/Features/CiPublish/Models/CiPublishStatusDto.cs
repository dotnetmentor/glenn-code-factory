namespace Source.Features.CiPublish.Models;

public record CiPublishStatusDto
{
    public string? DaemonStableGitSha { get; init; }
    public string? RuntimeActiveGitSha { get; init; }
    public bool DaemonPublishedForRequestedSha { get; init; }
    public bool RuntimePublishedForRequestedSha { get; init; }
}

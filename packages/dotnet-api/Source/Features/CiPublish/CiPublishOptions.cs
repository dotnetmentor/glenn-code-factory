namespace Source.Features.CiPublish;

public sealed class CiPublishOptions
{
    public const string SectionName = "CiPublish";

    /// <summary>
    /// Shared secret for GitHub Actions / automation. When empty, CiPublish auth is disabled.
    /// </summary>
    public string? ApiKey { get; set; }
}

namespace Source.Features.Cloudflare.Configuration;

/// <summary>
/// Strongly-typed binding for the <c>Cloudflare</c> SystemSettings section.
/// Values are sourced from
/// <see cref="Source.Features.SystemSettings.Services.ISystemSettingsService"/>
/// (DB-backed, cached) — never from <c>appsettings.json</c>. Mirrors
/// <see cref="Source.Features.FlyManagement.Configuration.FlyOptions"/> exactly
/// so every cloud-credential surface has the same shape.
/// </summary>
public class CloudflareOptions
{
    public const string SectionName = "Cloudflare";

    /// <summary>
    /// Cloudflare API token (encrypted at rest in SystemSettings; cleartext
    /// here on the in-memory accessor side). Used as
    /// <c>Authorization: Bearer {token}</c> on every Cloudflare API call.
    /// </summary>
    public string ApiToken { get; set; } = string.Empty;

    /// <summary>Cloudflare account id (32-char hex). Used in tunnel-create paths.</summary>
    public string AccountId { get; set; } = string.Empty;

    /// <summary>Cloudflare zone id for the base domain (32-char hex). Used in DNS-record paths.</summary>
    public string ZoneId { get; set; } = string.Empty;

    /// <summary>
    /// The apex domain under which preview subdomains are minted. Default
    /// <c>example.com</c>. Combined with an 8-char prefix to form the full
    /// hostname (e.g. <c>kj4m9x2p.example.com</c>).
    /// </summary>
    public string BaseDomain { get; set; } = "example.com";
}

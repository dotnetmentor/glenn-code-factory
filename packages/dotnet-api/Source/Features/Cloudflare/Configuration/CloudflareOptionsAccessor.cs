using Source.Features.SystemSettings.Services;

namespace Source.Features.Cloudflare.Configuration;

/// <summary>
/// Indirect-read façade for <see cref="CloudflareOptions"/>. Mirrors
/// <see cref="Source.Features.FlyManagement.Configuration.IFlyOptionsAccessor"/>
/// exactly so every cloud-credential consumer has the same constructor shape.
/// </summary>
public interface ICloudflareOptionsAccessor
{
    /// <summary>
    /// Snapshot of the Cloudflare configuration, materialised on each access by
    /// binding the cached <c>Cloudflare:*</c> keys onto a fresh
    /// <see cref="CloudflareOptions"/> instance. Cheap — hits the in-memory
    /// <c>SystemSettingsCache</c>.
    /// </summary>
    CloudflareOptions Current { get; }
}

public class CloudflareOptionsAccessor : ICloudflareOptionsAccessor
{
    private readonly ISystemSettingsService _settings;

    public CloudflareOptionsAccessor(ISystemSettingsService settings)
    {
        _settings = settings;
    }

    public CloudflareOptions Current
    {
        get
        {
            var opts = _settings.GetSection<CloudflareOptions>(CloudflareOptions.SectionName);
            // GetSection<T>'s reflection binder defaults blank/missing values to
            // empty strings — we want the documented default of "example.com"
            // when the operator has not yet entered anything.
            if (string.IsNullOrWhiteSpace(opts.BaseDomain))
            {
                opts.BaseDomain = "example.com";
            }
            return opts;
        }
    }
}

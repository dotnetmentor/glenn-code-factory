namespace Source.Features.BoxManagement.Configuration;

/// <summary>
/// Strongly-typed binding for the <c>Box</c> configuration section. Values are sourced
/// from <see cref="Source.Features.SystemSettings.Services.ISystemSettingsService"/>
/// (DB-backed, cached) — not from <c>appsettings.json</c>. Mirrors the pattern the
/// GitHub options use.
///
/// <para>Box (box.ascii.dev, by ASCII) hosts every project runtime as a persistent
/// full-VM sandbox. One account, one API key; boxes are namespaced per account and
/// tagged per-runtime via the env vars we stamp at fork time.</para>
/// </summary>
public class BoxOptions
{
    public const string SectionName = "Box";

    /// <summary>Box API key (created via <c>box api-key create</c>). Used as <c>Authorization: Bearer {key}</c>.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Base URL of the Box public API. Kept configurable so an API-host move never
    /// needs a redeploy. Trailing slash optional.
    /// </summary>
    public string ApiBaseUrl { get; set; } = "https://ascii.dev/api/box/v1";

    /// <summary>
    /// TTL stamped on every box we create or fork, in seconds. The TTL is the
    /// platform's orphan-cost guardrail: if the control plane ever loses track of a
    /// box, it auto-archives itself when the TTL lapses and billing stops. The
    /// <c>BoxTtlExtenderJob</c> re-extends the TTL for every runtime we still know
    /// about, so healthy boxes never hit it. Default: 6 hours.
    /// </summary>
    public int DefaultTtlSeconds { get; set; } = 21_600;

    /// <summary>
    /// Default Box machine type (tier) for new runtimes when the requested
    /// cpu/mem spec doesn't dictate one: <c>small</c> (2 vCPU / 4 GB),
    /// <c>default</c> (4 vCPU / 8 GB) or <c>large</c> (8 vCPU / 16 GB).
    /// (The wire field on create/fork/resume bodies is <c>type</c>.)
    /// </summary>
    public string DefaultType { get; set; } = "small";
}

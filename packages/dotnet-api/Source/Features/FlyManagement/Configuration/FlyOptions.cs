namespace Source.Features.FlyManagement.Configuration;

/// <summary>
/// Strongly-typed binding for the <c>Fly</c> configuration section. Values are sourced
/// from <see cref="Source.Features.SystemSettings.Services.ISystemSettingsService"/>
/// (DB-backed, cached) — not from <c>appsettings.json</c>. This mirrors the
/// <see cref="Source.Features.GitHub.Configuration.GithubOptions"/> pattern.
///
/// <para>The Fly.io Machines API uses a single Personal Access Token for auth and
/// an "app namespace" to group machines + volumes under one logical app. We run all
/// runtime instances under one shared Fly app, scoped per-runtime via labels/metadata.</para>
/// </summary>
public class FlyOptions
{
    public const string SectionName = "Fly";

    /// <summary>Fly Personal Access Token (PAT). Used as <c>Authorization: Bearer {token}</c>.</summary>
    public string ApiToken { get; set; } = string.Empty;

    /// <summary>Fly organization slug (e.g. <c>"personal"</c>) — namespaces app/volume creation.</summary>
    public string OrgSlug { get; set; } = string.Empty;

    /// <summary>The shared Fly app name that hosts every runtime instance.</summary>
    public string AppName { get; set; } = string.Empty;

    /// <summary>Default region code for new machines/volumes when caller doesn't specify one.</summary>
    public string DefaultRegion { get; set; } = "arn";

    /// <summary>
    /// Shared secret used to HMAC-verify incoming webhooks from Fly. Set the same string in the
    /// Fly dashboard. Empty disables verification (and the webhook controller hard-fails 500 to
    /// avoid silently accepting unverified events).
    /// </summary>
    public string WebhookSecret { get; set; } = string.Empty;
}

namespace Source.Features.RuntimeLifecycle.Configuration;

/// <summary>
/// Runtime-side configuration consumed by the provisioner when stamping Fly
/// machine env vars. <c>PublicApiUrl</c> is the publicly-reachable URL daemons
/// dial back at — both HTTP (for /api/...) and SignalR (/hubs/runtime). In dev
/// this is the Cloudflare tunnel hostname; in production it's the canonical
/// API hostname.
/// </summary>
public sealed class RuntimeOptions
{
    public const string SectionName = "Runtime";
    public string PublicApiUrl { get; set; } = string.Empty;
}

namespace Source.Features.SystemSettings;

/// <summary>
/// Schema (in code, not in DB) of every category and key the SystemSettings feature
/// knows about. The DB stores values; this static registry stores metadata
/// (display name, description, secret flag) and is the source of truth for the seeder
/// and the admin UI.
///
/// <para>Categories registered today: <c>GitHub</c> and <c>Fly</c>. Future categories
/// (Email, Storage, etc.) are added here as new <see cref="SystemSettingCategory"/>
/// entries — the underlying mechanism does not change.</para>
/// </summary>
public static class SystemSettingsCatalog
{
    public static IReadOnlyList<SystemSettingCategory> Categories { get; } = new[]
    {
        new SystemSettingCategory(
            Key: "GitHub",
            DisplayName: "GitHub",
            Description:
                "Configure your GitHub App credentials. These are read once on startup, " +
                "cached in memory, and used for OAuth login, App-installation flows, and webhook " +
                "signature validation.",
            Settings: new[]
            {
                new SystemSettingDefinition(
                    Key: "GitHub:AppId",
                    DisplayName: "App ID",
                    Description: "Numeric ID at the top of github.com/organizations/<org>/settings/apps/<app>.",
                    IsSecret: false),
                new SystemSettingDefinition(
                    Key: "GitHub:ClientId",
                    DisplayName: "Client ID",
                    Description: "Same settings page, under 'About'.",
                    IsSecret: false),
                new SystemSettingDefinition(
                    Key: "GitHub:ClientSecret",
                    DisplayName: "Client Secret",
                    Description: "Generate under 'Client secrets' on the App settings page.",
                    IsSecret: true),
                new SystemSettingDefinition(
                    Key: "GitHub:PrivateKeyPem",
                    DisplayName: "Private Key (PEM)",
                    Description: "Generate under 'Private keys'. Paste the whole -----BEGIN…-----END----- block.",
                    IsSecret: true),
                new SystemSettingDefinition(
                    Key: "GitHub:WebhookSecret",
                    DisplayName: "Webhook Secret",
                    Description: "Set under 'Webhook' → 'Secret'. Use the same string here.",
                    IsSecret: true),
                new SystemSettingDefinition(
                    Key: "GitHub:AppSlug",
                    DisplayName: "App Slug",
                    Description: "URL slug of the App, e.g. 'my-app' from github.com/apps/my-app.",
                    IsSecret: false),
                new SystemSettingDefinition(
                    Key: "GitHub:OAuthRedirectUri",
                    DisplayName: "OAuth Redirect URI",
                    Description: "Must match the 'User authorization callback URL' on the App. " +
                                 "Default: http://localhost:5338/api/github/login/callback",
                    IsSecret: false),
                new SystemSettingDefinition(
                    Key: "GitHub:AppInstallRedirectUri",
                    DisplayName: "App Install Redirect URI",
                    Description: "Must match the 'Setup URL' on the App. " +
                                 "Default: http://localhost:5338/api/github/install/callback",
                    IsSecret: false),
            }),
        new SystemSettingCategory(
            Key: "Fly",
            DisplayName: "Fly.io",
            Description:
                "Configure access to the Fly.io Machines API. Runtime instances (one per " +
                "user project) are provisioned as Fly machines under a single shared app. " +
                "Values are cached in memory after the first read.",
            Settings: new[]
            {
                new SystemSettingDefinition(
                    Key: "Fly:ApiToken",
                    DisplayName: "API Token",
                    Description: "Fly Personal Access Token (https://fly.io/user/personal_access_tokens)",
                    IsSecret: true),
                new SystemSettingDefinition(
                    Key: "Fly:OrgSlug",
                    DisplayName: "Org Slug",
                    Description: "Fly organization slug, e.g. 'personal'",
                    IsSecret: false),
                new SystemSettingDefinition(
                    Key: "Fly:AppName",
                    DisplayName: "App Name",
                    Description: "Single Fly app namespace for all runtimes (e.g. 'your-runtimes')",
                    IsSecret: false),
                new SystemSettingDefinition(
                    Key: "Fly:DefaultRegion",
                    DisplayName: "Default Region",
                    Description: "Default Fly region (e.g. 'arn' for Stockholm). Default: arn",
                    IsSecret: false),
                new SystemSettingDefinition(
                    Key: "Fly:WebhookSecret",
                    DisplayName: "Webhook Secret",
                    Description: "HMAC secret configured in Fly dashboard. Used to verify incoming webhook authenticity.",
                    IsSecret: true),
            }),
        new SystemSettingCategory(
            Key: "Runtime",
            DisplayName: "Runtime",
            Description:
                "Runtime-side configuration consumed by the provisioner when stamping Fly " +
                "machine env vars. Changes take effect on the next provisioner tick (≤ 1 minute) " +
                "without a process restart.",
            Settings: new[]
            {
                new SystemSettingDefinition(
                    Key: "Runtime:PublicApiUrl",
                    DisplayName: "Public API URL",
                    Description:
                        "Publicly-reachable URL daemons dial back at — both HTTP (/api/...) and " +
                        "SignalR (/hubs/runtime). In dev this is a Cloudflare tunnel hostname; in " +
                        "production it's the canonical API hostname. Example: " +
                        "https://api.example.com. Must include the scheme and have no trailing slash.",
                    IsSecret: false),
            }),
        new SystemSettingCategory(
            Key: "RuntimeLifecycle",
            DisplayName: "Runtime Lifecycle",
            Description:
                "Tunables for the runtime idle / wake / janitor pipeline. " +
                "Changes here affect background workers and take effect on the next scan iteration.",
            Settings: new[]
            {
                new SystemSettingDefinition(
                    Key: "RuntimeLifecycle:IdleThresholdMinutes",
                    DisplayName: "Idle Threshold (Minutes)",
                    Description: "How many minutes a runtime can stay idle (no active hub connections, no in-flight session) before the IdlerJob suspends it. Default: 30. Lower values aggressively save Fly machine cost; higher values keep tabs warm longer.",
                    IsSecret: false),
            }),
        new SystemSettingCategory(
            Key: "RuntimeTokens",
            DisplayName: "Runtime Tokens",
            Description:
                "HMAC signing keys for short-lived RuntimeToken JWTs. The Current key signs new tokens; " +
                "the Previous key is accepted for validation during a rotation window so in-flight tokens " +
                "minted with the old key keep working.",
            Settings: new[]
            {
                new SystemSettingDefinition(
                    Key: "RuntimeTokens:SigningKeyCurrent",
                    DisplayName: "Signing Key (Current)",
                    Description: "Current HMAC-SHA256 signing key for RuntimeToken JWTs (base64 of 32 random bytes). Auto-generated on first boot if missing. Rotate by moving the current value to Previous and setting a fresh Current.",
                    IsSecret: true),
                new SystemSettingDefinition(
                    Key: "RuntimeTokens:SigningKeyPrevious",
                    DisplayName: "Signing Key (Previous)",
                    Description: "Previous HMAC signing key (validation-only). Used during a rotation window so tokens minted with the old key still validate. Clear when the rotation window ends.",
                    IsSecret: true),
            }),
        new SystemSettingCategory(
            Key: "Cloudflare",
            DisplayName: "Cloudflare",
            Description:
                "Cloudflare account credentials used to provision per-branch preview tunnels. " +
                "The API token must be scoped at minimum to <c>Account: Cloudflare Tunnel: Edit</c> " +
                "and <c>Zone: DNS: Edit</c> for the configured zone — the platform creates one " +
                "Cloudflare Tunnel + DNS CNAME per pool subdomain. The token is encrypted at rest " +
                "(same AES-256-GCM envelope every secret <c>SystemSetting</c> uses) and is never " +
                "echoed back through the read endpoints — the admin UI surfaces a 'set / unset' flag " +
                "only.",
            Settings: new[]
            {
                new SystemSettingDefinition(
                    Key: "Cloudflare:ApiToken",
                    DisplayName: "API Token",
                    Description:
                        "Cloudflare API token with permissions to create tunnels (Account → Cloudflare " +
                        "Tunnel: Edit) and DNS records on the chosen zone (Zone → DNS: Edit). Generated " +
                        "under My Profile → API Tokens. Stored encrypted; the GET endpoint exposes a " +
                        "'set' indicator only — the raw value never leaves the server.",
                    IsSecret: true),
                new SystemSettingDefinition(
                    Key: "Cloudflare:AccountId",
                    DisplayName: "Account ID",
                    Description:
                        "Cloudflare account id (32-char hex). Found in the Cloudflare dashboard URL or " +
                        "under any zone's Overview pane on the right-hand side. Used in " +
                        "<c>/accounts/{account_id}/cfd_tunnel</c> calls.",
                    IsSecret: false),
                new SystemSettingDefinition(
                    Key: "Cloudflare:ZoneId",
                    DisplayName: "Zone ID",
                    Description:
                        "Cloudflare zone id for the base domain (32-char hex). Found on the Overview " +
                        "pane of the chosen zone. Used in <c>/zones/{zone_id}/dns_records</c> calls.",
                    IsSecret: false),
                new SystemSettingDefinition(
                    Key: "Cloudflare:BaseDomain",
                    DisplayName: "Base Domain",
                    Description:
                        "The apex domain under which preview subdomains are minted. Every pool " +
                        "subdomain becomes <c>{8-char-random}.{base-domain}</c>, e.g. " +
                        "<c>kj4m9x2p.example.com</c>. Must match the zone identified by Zone ID. " +
                        "Default: <c>example.com</c>.",
                    IsSecret: false),
            }),
        new SystemSettingCategory(
            Key: "AgentPermissions",
            DisplayName: "Agent Permissions",
            Description:
                "System-wide defaults for agent permission modes. These " +
                "values are used by every project that does not have its own override row. " +
                "The shipped defaults are YOLO-with-guardrails: bypassPermissions mode is on " +
                "(no approval prompts) and the dangerous-shell denylist hides the worst " +
                "offenders from the agent entirely (rm -rf, sudo, curl | sh, etc). None of " +
                "these fields are secrets.",
            Settings: new[]
            {
                new SystemSettingDefinition(
                    Key: "AgentPermissions:PermissionMode",
                    DisplayName: "Permission Mode",
                    Description:
                        "Controls when the agent prompts you before acting. One of: " +
                        "'default' (prompts before file edits, shell commands, and other risky " +
                        "actions), 'acceptEdits' (auto-approves file edits; prompts for other " +
                        "risky actions), 'bypassPermissions' (no prompts; agent acts freely — " +
                        "only DisallowedTools are blocked), 'plan' (agent plans without taking " +
                        "actions), 'dontAsk' (like default but suppresses prompts the SDK would " +
                        "otherwise raise). The approval prompt only fires for tools the SDK " +
                        "classifies as risky (Write, Edit, certain Bash commands). Note: 'auto' " +
                        "is intentionally NOT offered.",
                    IsSecret: false),
                new SystemSettingDefinition(
                    Key: "AgentPermissions:AllowDangerouslySkipPermissions",
                    DisplayName: "Allow Dangerously Skip Permissions",
                    Description:
                        "Mirrors the SDK's --dangerously-skip-permissions flag. Required true " +
                        "when PermissionMode is 'bypassPermissions'. Boolean stored as 'true' " +
                        "or 'false'.",
                    IsSecret: false),
                new SystemSettingDefinition(
                    Key: "AgentPermissions:AllowedTools",
                    DisplayName: "Allowed Tools",
                    Description:
                        "Tools the agent can always use without prompting. JSON array of " +
                        "tool-pattern strings (e.g. 'Read', 'Bash(npm test)'). Empty array means " +
                        "'no explicit allow-list'.",
                    IsSecret: false),
                new SystemSettingDefinition(
                    Key: "AgentPermissions:DisallowedTools",
                    DisplayName: "Disallowed Tools",
                    Description:
                        "Tools the agent cannot use. The model won't see or attempt these tools " +
                        "— they are hidden from its context entirely, so no approval prompt is " +
                        "raised for them. JSON array of tool-pattern strings. Wins over " +
                        "bypassPermissions. The shipped default blocks the classic dangerous " +
                        "shell patterns: rm -rf, sudo, curl-to-shell, fork bombs, raw-disk writes.",
                    IsSecret: false),
                new SystemSettingDefinition(
                    Key: "AgentPermissions:AdditionalDirectories",
                    DisplayName: "Additional Directories",
                    Description:
                        "JSON array of absolute paths the agent may read/write beyond its cwd. " +
                        "Empty array means 'cwd only'.",
                    IsSecret: false),
            }),
    };

    /// <summary>Flatten across categories — convenience for the seeder.</summary>
    public static IEnumerable<SystemSettingDefinition> AllSettings =>
        Categories.SelectMany(c => c.Settings);

    /// <summary>Look up a single key's metadata. Returns <c>null</c> if not registered.</summary>
    public static SystemSettingDefinition? FindByKey(string key)
        => AllSettings.FirstOrDefault(s => s.Key == key);
}

public record SystemSettingCategory(
    string Key,
    string DisplayName,
    string Description,
    IReadOnlyList<SystemSettingDefinition> Settings);

public record SystemSettingDefinition(
    string Key,
    string DisplayName,
    string Description,
    bool IsSecret)
{
    /// <summary>The category prefix (everything before the first colon).</summary>
    public string Category => Key.Split(':', 2)[0];
}

/// <summary>
/// Canonical default values for the <c>AgentPermissions</c> category. Kept in code
/// (rather than appsettings.json) because they represent the SDK contract — the
/// "YOLO-with-guardrails" baseline that ships fresh environments into a usable state.
/// The EF migration that seeds these rows references these constants directly so the
/// catalog and migration emit byte-identical payloads.
/// </summary>
public static class AgentPermissionsDefaults
{
    /// <summary>Default permission mode. See SDK docs for the semantics.</summary>
    public const string PermissionMode = "bypassPermissions";

    /// <summary>String "true" / "false" — stored as text like every other SystemSetting value.</summary>
    public const string AllowDangerouslySkipPermissions = "true";

    /// <summary>Empty JSON array. No explicit allow-list out of the box.</summary>
    public const string AllowedToolsJson = "[]";

    /// <summary>Empty JSON array. cwd-only out of the box.</summary>
    public const string AdditionalDirectoriesJson = "[]";

    /// <summary>
    /// The shipped denylist — the guardrails in YOLO-with-guardrails. Each entry is
    /// a tool-pattern string matching the agent permission grammar:
    /// <c>ToolName(arg-pattern)</c> with glob-style <c>*</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> DisallowedTools = new[]
    {
        "Bash(rm -rf /*)",
        "Bash(rm -rf ~)",
        "Bash(rm -rf .)",
        "Bash(sudo *)",
        "Bash(curl * | sh)",
        "Bash(curl * | bash)",
        "Bash(wget * | sh)",
        "Bash(wget * | bash)",
        "Bash(:(){ :|:& };:)",
        "Bash(dd if=/dev/zero *)",
        "Bash(mkfs.*)",
        "Bash(> /dev/sda*)",
    };

    /// <summary>
    /// JSON-serialized form of <see cref="DisallowedTools"/> as it lands in the
    /// <c>SystemSettings.Value</c> column. Computed once at type-init time so the
    /// catalog, migration, and resolver agree on the exact same string.
    /// </summary>
    public static readonly string DisallowedToolsJson =
        System.Text.Json.JsonSerializer.Serialize(DisallowedTools);
}

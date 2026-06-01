using Source.Features.RuntimeBootstrap.Contracts;
using Source.Shared;
using Source.Shared.Events;

namespace Source.Features.RuntimePresets.Models;

/// <summary>
/// Database-backed preset for the Runtime Spec V3 system. Each row defines a
/// template the agent (and humans in super admin) pick from when proposing a
/// runtime spec. The <see cref="Services.PresetExpander"/> (next card)
/// resolves <c>{{handlebars}}</c> placeholders in the command / env / setup
/// templates against the values the caller supplies in the V3 spec, producing
/// the daemon-bound <c>ServiceSpec</c>.
///
/// <para><b>Why DB, not code.</b> V2's hardcoded preset shape was an operator
/// dead-end — every new toolchain / version meant a redeploy. V3 lets a super
/// admin clone a seeded preset and tweak it (e.g. pin a different .NET
/// version, add an env var) without shipping a release.</para>
///
/// <para><b>jsonb columns as <see cref="string"/>.</b> Same convention as
/// <c>Project.Spec</c>, <c>RuntimeProposal.ProposedSpec</c> — entity holds the
/// raw JSON string, callers serialise / deserialise via
/// <see cref="System.Text.Json.JsonSerializer"/>. Keeps EF Core's relational
/// provider happy and means tests on EF InMemory still round-trip.</para>
///
/// <para><b>IsBuiltIn semantics.</b> Seeded rows are tagged <c>true</c>; the
/// admin UI hides edit / delete on these and exposes "Clone" instead, so
/// operators tweak a copy and the originals stay intact across deploys.</para>
///
/// <para><b>Soft delete.</b> Implements <see cref="ISoftDelete"/> so a
/// retired preset still appears in audit trails (existing proposals referenced
/// it). <see cref="ApplicationDbContext"/> registers a global query filter
/// excluding deleted rows from normal reads.</para>
/// </summary>
public class ServicePreset : Entity, IAuditable, ISoftDelete
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Stable lower-kebab identifier referenced by <c>ServiceInstance.kind</c>
    /// in a V3 spec (e.g. <c>"dotnet-mise"</c>, <c>"node-vite"</c>). Unique
    /// index in <c>OnModelCreating</c>. The agent's <c>propose_runtime_spec</c>
    /// tool description enumerates these slugs in its <c>oneOf</c> schema.
    /// </summary>
    public required string Slug { get; set; }

    /// <summary>
    /// Human-readable name for the super admin gallery
    /// (e.g. <c>".NET (mise toolchain)"</c>).
    /// </summary>
    public required string DisplayName { get; set; }

    /// <summary>
    /// One-paragraph description shown in the admin UI and embedded in the
    /// agent's dynamically-built tool description.
    /// </summary>
    public required string Description { get; set; }

    /// <summary>
    /// Bucket used by the gallery's category tabs. Persisted as int via
    /// <c>HasConversion&lt;int&gt;()</c> — same on-disk shape as
    /// <c>RuntimeProposal.Status</c>.
    /// </summary>
    public PresetCategory Category { get; set; }

    /// <summary>
    /// Optional MUI icon name (e.g. <c>"Code"</c>, <c>"Storage"</c>) the
    /// gallery card renders. Null falls back to a category-default glyph in
    /// the frontend.
    /// </summary>
    public string? IconName { get; set; }

    /// <summary>
    /// True for the migration-seeded presets — the admin UI locks edit /
    /// delete on these and exposes "Clone" instead so operators tweak a copy
    /// while the originals survive future deploys.
    /// </summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>
    /// The <c>command</c> field for the rendered daemon-bound
    /// <c>ServiceSpec</c>. May contain <c>{{handlebars}}</c> placeholders —
    /// the expander substitutes them against user-supplied <c>values</c>
    /// merged with parameter defaults.
    ///
    /// <para>All system-injected values (currently just <c>{{repoDir}}</c>,
    /// which always resolves to <c>/data/project/repo</c>) are added by the
    /// expander automatically; they do NOT appear as <see cref="Parameters"/>
    /// rows.</para>
    /// </summary>
    public required string CommandTemplate { get; set; }

    /// <summary>
    /// jsonb-stored <c>Dictionary&lt;string,string&gt;</c> — the <c>env</c>
    /// block for the rendered ServiceSpec. Values may contain
    /// <c>{{handlebars}}</c> placeholders; the expander renders each value
    /// individually.
    ///
    /// <para>Stored as <see cref="string"/> on the CLR side (same pattern as
    /// <c>Project.Spec</c>) — the expander deserialises with
    /// <c>System.Text.Json.JsonSerializer</c>.</para>
    /// </summary>
    public required string EnvTemplate { get; set; }

    /// <summary>
    /// Optional shell command run by supervisord to decide whether the
    /// service is healthy. Same <c>{{handlebars}}</c> substitution rules as
    /// <see cref="CommandTemplate"/>. Null means "no healthcheck" — the
    /// daemon falls back to "RUNNING after 2s = healthy".
    /// </summary>
    public string? HealthcheckCommand { get; set; }

    /// <summary>
    /// Seconds between healthcheck runs. Null inherits the daemon default
    /// (5s). Kept nullable so the seed JSON can omit the field.
    /// </summary>
    public int? HealthcheckInterval { get; set; }

    /// <summary>
    /// User the supervisord program runs as. Defaults to <c>"agent"</c> for
    /// app presets, <c>"postgres"</c> / etc. for service presets.
    /// </summary>
    public string? DefaultUser { get; set; }

    /// <summary>
    /// Mirror of supervisord's <c>autorestart</c>. Default true — almost
    /// every preset wants this; the exceptions are one-shot init scripts
    /// (none seeded today).
    /// </summary>
    public bool Autorestart { get; set; } = true;

    /// <summary>
    /// Optional snippet contributed to the spec's top-level <c>install</c>
    /// block. Multiple instances of the same preset in one spec dedupe to a
    /// single contribution (hash-matched by the expander) so e.g. installing
    /// mise once for two dotnet services doesn't run twice.
    /// </summary>
    public string? InstallContribution { get; set; }

    /// <summary>
    /// Optional snippet contributed to the spec's top-level <c>setup</c>
    /// block. Same dedupe rules as <see cref="InstallContribution"/>. This is
    /// where heavy work (restore + build, npm install, initdb) lives — moving
    /// it out of the service <c>command</c> is the core fix that prevents
    /// 180s healthcheck timeouts.
    /// </summary>
    public string? SetupContribution { get; set; }

    /// <summary>
    /// Optional shell command run after <c>install</c> to verify the toolchain
    /// is on PATH. Failure aborts the runtime bootstrap with a clear error
    /// instead of waiting 180s for the broken service to time out.
    /// </summary>
    public string? InstallVerify { get; set; }

    /// <summary>
    /// jsonb-stored <c>List&lt;RequiredEnvVar&gt;</c> — the environment variables
    /// this preset's service <em>declares it needs</em>. Declaring a var here
    /// does NOT set it; the expander merges this list (plus any ad-hoc vars on
    /// the V3 service entry) into <c>ServiceSpec.RequiredEnv</c>, deduped by
    /// key, so the UI can flag "required but not set".
    ///
    /// <para>Nullable — most presets declare nothing here. Stored as a raw JSON
    /// string on the CLR side (same pattern as <see cref="EnvTemplate"/> /
    /// <see cref="Parameters"/>); the expander deserialises with
    /// <see cref="DeserializeRequiredEnv"/>. The <c>description</c> of each
    /// entry may contain <c>{{handlebars}}</c> placeholders rendered by the
    /// expander against the standard template bag.</para>
    /// </summary>
    public string? RequiredEnvContribution { get; set; }

    /// <summary>
    /// JSON options for the <see cref="RequiredEnvContribution"/> jsonb column —
    /// camelCase property names so the on-disk shape matches the V2/V3
    /// <c>RequiredEnvVar</c> wire shape (<c>key</c> / <c>description</c> /
    /// <c>secret</c>) and the migration seed SQL reads cleanly.
    /// </summary>
    public static readonly System.Text.Json.JsonSerializerOptions RequiredEnvJsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Convenience deserializer for the jsonb <see cref="RequiredEnvContribution"/>
    /// column. Returns an empty list (never null) when the raw JSON is null /
    /// whitespace / a JSON null literal. Mirrors
    /// <see cref="PresetParameter.DeserializeList"/>.
    /// </summary>
    public static List<RequiredEnvVar> DeserializeRequiredEnv(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<RequiredEnvVar>();
        }
        return System.Text.Json.JsonSerializer.Deserialize<List<RequiredEnvVar>>(
                   json, RequiredEnvJsonOptions)
               ?? new List<RequiredEnvVar>();
    }

    /// <summary>
    /// jsonb-stored <c>List&lt;PresetParameter&gt;</c> — the parameter schema
    /// the admin UI renders as a form and the agent's tool input schema uses
    /// to build per-kind <c>values</c> validation. Stored as raw JSON string
    /// on the CLR side; the expander / UI deserialise with
    /// <see cref="System.Text.Json.JsonSerializer"/>.
    ///
    /// <para>Stored as JSON-with-string-enum (not numeric) so the seed SQL in
    /// the migration is readable — see
    /// <see cref="PresetParameter.JsonOptions"/>.</para>
    /// </summary>
    public required string Parameters { get; set; }

    // -------- IAuditable --------
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // -------- ISoftDelete --------
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
}

/// <summary>
/// Gallery bucket. Persisted as int via <c>HasConversion&lt;int&gt;()</c>;
/// reorder additions are safe because the int values are pinned.
/// </summary>
public enum PresetCategory
{
    Backend = 0,
    Frontend = 1,
    Database = 2,
    Worker = 3,
    Other = 4,
}

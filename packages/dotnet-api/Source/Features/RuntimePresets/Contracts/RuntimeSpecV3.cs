using System.Text.Json;
using System.Text.Json.Serialization;
using Source.Features.RuntimeBootstrap.Contracts;
using Source.Shared.Results;
using Tapper;

namespace Source.Features.RuntimePresets.Contracts;

/// <summary>
/// V3 of the runtime spec — the preset-based input shape that the agent (and
/// the super-admin UI) authors against, before the
/// <see cref="Services.IPresetExpander"/> expands it into the V2-shaped wire
/// format the daemon already consumes.
///
/// <para><b>Why V3.</b> V2 made the operator (or AI) write the entire
/// supervisord program block from scratch: command line, env block, install
/// bash, healthcheck command. That worked for the first few projects but
/// pushed every new toolchain / framework into the agent's prompt budget and
/// surfaced as 180s healthcheck timeouts whenever the AI guessed the wrong
/// command. V3 introduces a DB-backed library of <em>presets</em> (one row
/// per "kind" like <c>dotnet-mise</c>, <c>node-vite</c>, <c>postgres-15</c>)
/// with handlebars templates and a typed parameter schema; the V3 spec only
/// references presets by slug and supplies the values for their parameters.
/// The expander does the rest, deterministically.</para>
///
/// <para><b>Shape.</b> Top-level <see cref="Install"/> / <see cref="Setup"/> /
/// <see cref="InstallVerify"/> mirror V2 — the operator can still inject
/// freeform bash that isn't tied to a single preset (e.g. shared
/// system-wide apt installs). The new field is
/// <see cref="Services"/>: each entry picks a preset by <see cref="ServiceInstance.Kind"/>
/// and supplies its <see cref="ServiceInstance.Values"/>.</para>
///
/// <para><b>Values shape.</b> <see cref="ServiceInstance.Values"/> uses
/// <see cref="JsonElement"/> as the value type so a number stays a number,
/// a bool stays a bool, etc. (no boxing through <c>object</c>, no string
/// coercion on the way in). The expander converts to string for handlebars
/// substitution at render time.</para>
///
/// <para><b>System-injected placeholders.</b> <c>{{repoDir}}</c> always
/// resolves to <c>/data/project/repo</c> (the canonical repo root inside the
/// runtime container) — never appears in a preset's
/// <c>Parameters</c> schema, but is available in every template. Added by
/// the expander; the V3 author should not try to set it.</para>
/// </summary>
[TranspilationSource]
public sealed record RuntimeSpecV3
{
    /// <summary>
    /// Schema version discriminator. Always <c>3</c> for this record. The
    /// platform routes on this when reading <c>Project.Spec</c>: 1 → legacy
    /// V1 catalog, 2 → freeform V2, 3 → preset-based V3 (expand before push
    /// to daemon).
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; init; } = 3;

    /// <summary>
    /// Top-level install bash, concatenated with each preset's
    /// <c>InstallContribution</c> (deduped, in spec order) to form the final
    /// install blob. Null / empty means "presets supply everything, nothing
    /// extra on top".
    /// </summary>
    [JsonPropertyName("install")]
    public string? Install { get; init; }

    /// <summary>
    /// Top-level install-verify bash, concatenated with each preset's
    /// <c>InstallVerify</c> (deduped, in spec order) into the verification
    /// step the daemon runs on the install-skip path. Same belt-and-suspenders
    /// purpose as V2 — guards against host migrations that wipe the rootfs
    /// while leaving the install-hash store intact.
    /// </summary>
    [JsonPropertyName("installVerify")]
    public string? InstallVerify { get; init; }

    /// <summary>
    /// Top-level setup bash, concatenated with each preset's
    /// <c>SetupContribution</c> (deduped, in spec order). Runs every boot
    /// after install / install-skip and before services start.
    /// </summary>
    [JsonPropertyName("setup")]
    public string? Setup { get; init; }

    /// <summary>
    /// Service instances to provision. Required and non-empty — a V3 spec
    /// with no services is meaningless (use V2 if all you want is install +
    /// setup with no managed processes).
    /// </summary>
    [JsonPropertyName("services")]
    public List<ServiceInstance>? Services { get; init; }

    /// <summary>
    /// JSON options matching the V3 wire shape: camelCase property names,
    /// nulls omitted on write, enums round-trip as strings. Used by
    /// <see cref="ToJson"/> and <see cref="TryParse"/> so the on-disk shape
    /// is stable and human-readable in the <c>Project.Spec</c> jsonb column.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Serialise this spec to JSON suitable for writing into the
    /// <c>Project.Spec</c> jsonb column or a <c>RuntimeProposal.ProposedSpec</c>.
    /// </summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// Parse a V3 spec JSON body. Returns <c>null</c> on any malformed input —
    /// callers decide whether to fall through to V2 parsing or surface the
    /// failure. Use <see cref="Validate"/> afterwards to enforce structural
    /// invariants beyond "the JSON shape parsed".
    /// </summary>
    public static RuntimeSpecV3? TryParse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<RuntimeSpecV3>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Validate the spec's structural invariants without resolving presets:
    /// version is 3, services is non-empty, every service has a kind + name,
    /// service names are unique (case-insensitive — they become supervisord
    /// program names and we don't want look-alike pairs).
    ///
    /// <para>Preset existence and parameter validation happen later in
    /// <see cref="Services.IPresetExpander.ExpandAsync"/> because they need
    /// the DB. Keep this call cheap and pure.</para>
    /// </summary>
    public Result Validate()
    {
        if (Version != 3)
        {
            return Result.Failure("spec_version_not_3");
        }
        if (Services is null || Services.Count == 0)
        {
            return Result.Failure("spec_services_required");
        }
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var svc in Services)
        {
            if (string.IsNullOrWhiteSpace(svc.Kind))
            {
                return Result.Failure("service_kind_required");
            }
            if (string.IsNullOrWhiteSpace(svc.Name))
            {
                return Result.Failure("service_name_required");
            }
            if (!seenNames.Add(svc.Name))
            {
                return Result.Failure($"service_name_duplicate:{svc.Name}");
            }
        }
        return Result.Success();
    }
}

/// <summary>
/// One service instance in a V3 spec. Identifies the preset to expand
/// (<see cref="Kind"/>), gives the resulting supervisord program a
/// (<see cref="Name"/>), and supplies the parameter values
/// (<see cref="Values"/>) the preset's templates need.
///
/// <para><b>Why <see cref="JsonElement"/> for values.</b> We want number /
/// string / bool to round-trip through JSON without forcing the caller to
/// stringify on the way in. The expander converts to string at render time
/// where it has the parameter's declared type and can choose the right
/// rendering (port <c>5338</c> stays <c>5338</c>, not <c>"5338"</c>).</para>
/// </summary>
[TranspilationSource]
public sealed record ServiceInstance
{
    /// <summary>
    /// Preset slug to expand — matches <c>ServicePreset.Slug</c> in the DB.
    /// The expander fails with <c>preset_not_found:{slug}</c> if no row
    /// matches. Built-in slugs today: <c>dotnet-mise</c>, <c>node-vite</c>,
    /// <c>node-script</c>, <c>postgres-15</c>, <c>bash-raw</c>.
    /// </summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>
    /// Unique-within-spec supervisord program name for the expanded service.
    /// Two instances of the same preset (e.g. two <c>node-script</c>s) must
    /// have distinct names — the expander enforces this in
    /// <see cref="RuntimeSpecV3.Validate"/>.
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// Parameter values keyed by the preset's <c>PresetParameter.Key</c>.
    /// Null / missing keys fall back to the preset's parameter
    /// <c>DefaultValue</c>; if a required parameter has neither a supplied
    /// value nor a default, the expander returns
    /// <c>param_required:{serviceName}.{paramKey}</c>.
    /// </summary>
    [JsonPropertyName("values")]
    public Dictionary<string, JsonElement>? Values { get; init; }

    /// <summary>
    /// Ad-hoc environment variables this service instance declares it needs,
    /// <em>beyond</em> whatever its preset already declares. Lets the agent
    /// flag a required var the preset doesn't know about (e.g. a project-
    /// specific API key) without editing the preset. Declaring a var here does
    /// NOT set it — it only marks it required so the UI shows "required but not
    /// set".
    ///
    /// <para>The expander merges this list with the preset's
    /// <c>RequiredEnvContribution</c> into the expanded
    /// <c>ServiceSpec.RequiredEnv</c>, deduped by
    /// <see cref="RequiredEnvVar.Key"/> (first-wins).
    /// A dedicated typed field (not stuffed into <see cref="Values"/>) because
    /// these are declarations, not template parameters.</para>
    /// </summary>
    [JsonPropertyName("requiredEnv")]
    public List<RequiredEnvVar>? RequiredEnv { get; init; }
}

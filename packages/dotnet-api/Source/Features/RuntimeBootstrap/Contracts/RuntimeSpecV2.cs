using System.Text.Json;
using System.Text.Json.Serialization;
using Source.Shared.Results;
using Tapper;

namespace Source.Features.RuntimeBootstrap.Contracts;

/// <summary>
/// V2 of the runtime spec — the daemon's wire format. Post-V3 cutover this is
/// no longer the user-facing input shape; it's the internal "expanded" format
/// produced by
/// <see cref="Source.Features.RuntimePresets.Services.IPresetExpander"/> at
/// proposal time and shipped to the daemon over the existing
/// <c>ApplyRuntimeSpecDelta</c> SignalR channel. User-facing input is
/// <see cref="Source.Features.RuntimePresets.Contracts.RuntimeSpecV3"/>
/// (preset-based). The name <c>RuntimeSpecV2</c> is retained to minimise
/// churn — conceptually this is just the daemon's contract.
///
/// <para><b>Shape.</b> Three top-level fields, all optional except
/// <see cref="Version"/>:</para>
/// <list type="bullet">
///   <item><see cref="Install"/> — top-level bash run once, hash-skipped on
///         subsequent boots. Typical contents: <c>apt-get install -y mongodb-org</c>,
///         <c>curl … | sh</c>. The install-hash cache lives in Phase 2;
///         the field is defined here so the contract is forward-stable.</item>
///   <item><see cref="Services"/> — list of long-running processes to
///         supervise. Each <see cref="ServiceSpec"/> renders to one
///         supervisord program block. Empty / null means "no managed
///         services" (e.g. a frontend-only project).</item>
///   <item><see cref="Setup"/> — bash run every boot (after install, before
///         services). Typical contents: <c>npm install</c>, <c>migrate up</c>.
///         NOT hash-cached — re-runs every time so checkout-fresh repos always
///         hydrate.</item>
/// </list>
///
/// <para><b>Validation.</b> Use <see cref="Validate"/> before persisting or
/// pushing to a daemon — it enforces the contract's invariants (non-empty
/// service names, unique names within the spec, non-empty commands).</para>
///
/// <para><b>Out of scope here.</b> Per-service inter-dependencies / ordering,
/// resource limits, and the actual install-hash execution logic all live in
/// follow-up cards.</para>
/// </summary>
[TranspilationSource]
public record RuntimeSpecV2
{
    /// <summary>
    /// Schema version discriminator. Always <c>2</c> for this record type.
    /// V3 (preset-based, user-facing) lives in
    /// <see cref="Source.Features.RuntimePresets.Contracts.RuntimeSpecV3"/>;
    /// the expander produces V2 from V3 at proposal time.
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; init; } = 2;

    /// <summary>
    /// Top-level install bash. Runs ONCE per spec hash — the daemon stores
    /// the SHA of this string after a successful run and skips subsequent
    /// boots that hash to the same value. Null / empty means "no install
    /// step". Hash-skip execution itself is Phase 2 work; this field exists
    /// today so the contract is stable when that ships.
    /// </summary>
    [JsonPropertyName("install")]
    public string? Install { get; init; }

    /// <summary>
    /// Optional verification predicate run on the install-skip path — the
    /// belt-and-suspenders safety net for the ~1% host-migration case where
    /// even <c>persist_rootfs="always"</c> doesn't save us. Bash command,
    /// exit-0 = healthy (skip is honoured), non-zero = the rootfs was wiped
    /// beneath us and the daemon re-runs <see cref="Install"/> regardless of
    /// the hash match.
    ///
    /// <para><b>Why.</b> Fly machines run on overlayfs: read-only image layer
    /// + writable upper layer (the rootfs). <c>persist_rootfs="always"</c>
    /// keeps the upper layer across machine update / scale-to-zero wake, but
    /// NOT across host maintenance (Fly may migrate the machine to a new
    /// host, which recreates the upper layer from the image). Our
    /// install-hash store lives on <c>/data</c> (a separate volume) and
    /// survives ANY rootfs wipe — so without a verifier the hash lies
    /// "already installed" while the binaries are gone, leaving supervisord
    /// trying to exec missing executables (FATAL loop).</para>
    ///
    /// <para><b>Semantics.</b> Null / empty = no verification (preserves
    /// pre-installVerify behaviour: trust the hash). Set to something cheap
    /// and authoritative like <c>command -v mongod</c>, <c>[ -x /usr/sbin/mariadbd ]</c>,
    /// or <c>which redis-server</c>. The daemon runs it via <c>bash -c</c>
    /// with the same PATH the install bash gets — no special shell features
    /// required.</para>
    ///
    /// <para><b>Scope.</b> Top-level verify covers the top-level install. For
    /// per-service verification, set <see cref="ServiceSpec.InstallVerify"/>
    /// on each service. The daemon's InstallStage walks them all on every
    /// skip path; ANY non-zero exit triggers a full re-install (we don't
    /// surgically re-run just the failing scope because installs often have
    /// cross-scope ordering: top-level mise toolchain, then service
    /// <c>initdb</c>, …).</para>
    /// </summary>
    [JsonPropertyName("installVerify")]
    public string? InstallVerify { get; init; }

    /// <summary>
    /// Services to supervise. Each entry renders to a supervisord program
    /// block. Null or empty list means "no managed services" — valid for
    /// projects that only need install + setup (e.g. a static frontend).
    /// </summary>
    [JsonPropertyName("services")]
    public List<ServiceSpec>? Services { get; init; }

    /// <summary>
    /// Per-boot setup bash. Runs every boot AFTER install completes / is
    /// skipped, BEFORE services start. Typical contents: <c>npm install</c>,
    /// database migrations. Not hash-cached — re-runs unconditionally so a
    /// freshly-cloned repo always hydrates.
    /// </summary>
    [JsonPropertyName("setup")]
    public string? Setup { get; init; }

    /// <summary>
    /// Default JSON options matching the application's controller config
    /// (camelCase, ignore-null-on-write, string enums). Use this for
    /// roundtrip serialisation against the Spec jsonb column so the wire
    /// shape matches the rest of the API.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Parse a spec JSON body into a <see cref="RuntimeSpecV2"/>. Returns
    /// failure on malformed JSON or shape mismatch — callers decide whether
    /// to fall through to V1 parsing or surface the error to the user.
    /// </summary>
    public static Result<RuntimeSpecV2> TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Result.Failure<RuntimeSpecV2>("spec_empty");
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<RuntimeSpecV2>(json, JsonOptions);
            if (parsed is null)
            {
                return Result.Failure<RuntimeSpecV2>("spec_null_deserialise");
            }
            return Result.Success(parsed);
        }
        catch (JsonException ex)
        {
            return Result.Failure<RuntimeSpecV2>($"spec_malformed: {ex.Message}");
        }
    }

    /// <summary>
    /// Serialise this spec to a JSON string suitable for writing into the
    /// <c>ProjectRuntime.Spec</c> jsonb column. Matches the controller-layer
    /// camelCase convention so the on-disk shape is consistent with API
    /// responses that emit the same record.
    /// </summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// Validate the spec's structural invariants. Returns success when:
    /// <list type="bullet">
    ///   <item>Every service has a non-empty <see cref="ServiceSpec.Name"/>.</item>
    ///   <item>Every service has a non-empty <see cref="ServiceSpec.Command"/>.</item>
    ///   <item>Service names are unique within the spec (case-sensitive — supervisord
    ///         program names are case-sensitive identifiers).</item>
    /// </list>
    /// On failure returns the first violation with a stable error code prefix
    /// so callers can pattern-match (<c>service_name_required</c>,
    /// <c>service_command_required</c>, <c>service_name_duplicate: {name}</c>).
    ///
    /// <para>For structured failure context suitable for the
    /// <c>SpecValidationFailed</c> observability event (audit item A6), call
    /// <see cref="ValidateDetailed"/> instead — same rules, but returns the
    /// failing field's JSON path and a human message alongside the stable code.</para>
    /// </summary>
    public Result Validate()
    {
        // ValidateDetailed ALWAYS succeeds (the act of validating worked); the
        // validation failure, if any, is carried in Value (null = spec is valid).
        // Checking IsSuccess here was a bug — it's always true, so Validate never
        // reported an invalid spec. Inspect Value instead.
        var detailed = ValidateDetailed();
        return detailed.Value is null
            ? Result.Success()
            : Result.Failure(detailed.Value.Code);
    }

    /// <summary>
    /// Structured variant of <see cref="Validate"/>. Returns
    /// <see cref="Result.Success{T}"/> with a <c>null</c> failure on a valid
    /// spec, or <see cref="Result.Failure{T}"/> with the same stable code
    /// string Validate() returns (for back-compat with existing callers and
    /// pattern-matchers). The Success path returns a non-null
    /// <see cref="SpecValidationFailure"/> only when validation fails; on
    /// success the value is null.
    ///
    /// <para><b>Why both shapes.</b> The legacy
    /// <see cref="Validate"/> contract is "Result with a string code", which
    /// keeps a long tail of pattern-match callers working. The new contract
    /// shape — <c>{ path, reason, message }</c> — is what the observability
    /// event payload needs (audit item A6). Wrapping it in a Result whose
    /// Value is the failure (or null on success) lets callers ergonomically
    /// destructure both: <c>if (!detailed.IsSuccess || detailed.Value is null)</c>
    /// for "spec is valid".</para>
    /// </summary>
    public Result<SpecValidationFailure?> ValidateDetailed()
    {
        if (Services is null || Services.Count == 0)
        {
            return Result.Success<SpecValidationFailure?>(null);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Services.Count; i++)
        {
            var svc = Services[i];

            if (string.IsNullOrWhiteSpace(svc.Name))
            {
                return Result.Success<SpecValidationFailure?>(new SpecValidationFailure(
                    Path: $"services[{i}].name",
                    Code: "service_name_required",
                    Message: "Service name is required and must be non-empty."));
            }
            if (string.IsNullOrWhiteSpace(svc.Command))
            {
                return Result.Success<SpecValidationFailure?>(new SpecValidationFailure(
                    Path: $"services[{i}].command",
                    Code: $"service_command_required: {svc.Name}",
                    Message: $"Service '{svc.Name}' is missing a command — supervisord needs something to exec."));
            }
            if (!seen.Add(svc.Name))
            {
                return Result.Success<SpecValidationFailure?>(new SpecValidationFailure(
                    Path: $"services[{i}].name",
                    Code: $"service_name_duplicate: {svc.Name}",
                    Message: $"Duplicate service name '{svc.Name}' — supervisord program names must be unique within a spec."));
            }
        }

        return Result.Success<SpecValidationFailure?>(null);
    }
}

/// <summary>
/// Structured failure carrier for <see cref="RuntimeSpecV2.ValidateDetailed"/>.
/// Powers the <c>SpecValidationFailed</c> runtime event payload shape used by
/// the super-admin observability surface (audit item A6):
/// <c>{ path, reason, message }</c>. The names <c>path</c> and <c>reason</c>
/// on the wire are the documented event-payload field names; <see cref="Path"/>
/// and <see cref="Code"/> here map to those one-for-one.
/// </summary>
/// <param name="Path">JSON path to the failing field, e.g. <c>services[2].command</c>.</param>
/// <param name="Code">Stable machine code — same string Validate() returns, e.g.
/// <c>service_command_required: api</c>.</param>
/// <param name="Message">Human-readable explanation rendered in the drawer.</param>
public record SpecValidationFailure(string Path, string Code, string Message);

/// <summary>
/// One supervised process inside the runtime container. Renders to a single
/// supervisord program block; the daemon owns the translation.
///
/// <para><b>Required:</b> <see cref="Name"/> (unique within the spec) and
/// <see cref="Command"/>. Everything else is optional with sensible defaults
/// applied by the daemon.</para>
/// </summary>
[TranspilationSource]
public record ServiceSpec
{
    /// <summary>
    /// Unique-within-spec identifier. Becomes the supervisord program name
    /// and the key the daemon uses to address this service (logs, restart,
    /// status). Required, non-empty.
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// The actual command supervisord should run, e.g.
    /// <c>postgres -D /var/lib/postgresql/data</c>. Required, non-empty.
    /// The daemon does NOT shell-interpret this; it's passed to supervisord
    /// as the program command directly.
    /// </summary>
    [JsonPropertyName("command")]
    public required string Command { get; init; }

    /// <summary>
    /// Unix user the process runs as. Null means "use the runtime default"
    /// (<c>agent</c>). Specify <c>root</c> only when genuinely required —
    /// most services should drop privileges.
    /// </summary>
    [JsonPropertyName("user")]
    public string? User { get; init; }

    /// <summary>
    /// Whether supervisord should automatically restart this process on
    /// non-zero exit. Null means "use the daemon default" (<c>true</c>).
    /// Set <c>false</c> for one-shot helpers that legitimately exit.
    /// </summary>
    [JsonPropertyName("autorestart")]
    public bool? Autorestart { get; init; }

    /// <summary>
    /// Per-service environment variables, merged on top of the runtime's
    /// inherited environment. Null / empty means "inherit only". Values are
    /// passed verbatim to supervisord — no secret resolution happens here;
    /// secrets flow through the separate ProjectSecrets pipeline.
    /// </summary>
    [JsonPropertyName("env")]
    public Dictionary<string, string>? Env { get; init; }

    /// <summary>
    /// Optional health-check definition. When set, the daemon polls this
    /// command and surfaces a <c>ServiceHealthy</c> / <c>ServiceUnhealthy</c>
    /// runtime event based on its exit code. Null means "no active health
    /// probe; rely on supervisord's process-alive signal".
    /// </summary>
    [JsonPropertyName("healthcheck")]
    public HealthcheckSpec? Healthcheck { get; init; }

    /// <summary>
    /// Per-service install bash, run during the install stage alongside
    /// the top-level <see cref="RuntimeSpecV2.Install"/>. Useful for
    /// service-scoped setup (e.g. <c>initdb</c> for postgres). Hash-skipped
    /// per-service just like the top-level install.
    /// </summary>
    [JsonPropertyName("install")]
    public string? Install { get; init; }

    /// <summary>
    /// Per-service verification predicate — runs on the install-skip path
    /// for THIS service. Same semantics as
    /// <see cref="RuntimeSpecV2.InstallVerify"/>: bash command, exit-0 =
    /// healthy (skip honoured), non-zero = re-run install regardless of the
    /// per-service hash match.
    ///
    /// <para><b>Typical use.</b> Verify the service's primary binary exists
    /// after a rootfs-wipe-since-last-boot. Examples:
    /// <c>command -v mongod</c> (after a top-level
    /// <c>apt-get install mongodb-org</c>),
    /// <c>[ -x /usr/sbin/mariadbd ]</c>, <c>command -v redis-server</c>.</para>
    ///
    /// <para><b>Scope.</b> A non-zero exit here forces re-execution of the
    /// WHOLE install blob (top-level + every service in spec order), not
    /// just this service. Surgical per-scope re-runs would skip cross-scope
    /// dependencies — e.g. a service install that assumes a mise toolchain
    /// from the top-level step. Cheap to over-install; expensive to
    /// under-install.</para>
    /// </summary>
    [JsonPropertyName("installVerify")]
    public string? InstallVerify { get; init; }

    /// <summary>
    /// Environment variables this service <em>declares it needs</em> in order
    /// to run correctly. Declaring a var here does NOT create or set it —
    /// it only flags the var as required so the UI can surface a
    /// "required but not set" indicator and pre-seed the secret editor.
    /// Null / empty means "this service declares no required env vars".
    ///
    /// <para><b>Provenance.</b> Populated by the expander from two sources,
    /// deduped by <see cref="RequiredEnvVar.Key"/>: the preset's
    /// <c>RequiredEnvContribution</c> plus any ad-hoc vars the agent declared
    /// on the V3 service entry. Distinct from <see cref="Env"/>, which carries
    /// concrete values supervisord injects — this list is purely declarative.</para>
    /// </summary>
    [JsonPropertyName("requiredEnv")]
    public List<RequiredEnvVar>? RequiredEnv { get; init; }
}

/// <summary>
/// A single environment variable a service declares it requires. Purely a
/// signal to the UI — declaring a var here flags it as "needed" so the
/// project-secrets editor can show it as required-but-missing and default the
/// secret toggle. Carrying a <see cref="RequiredEnvVar"/> never sets a value;
/// concrete values flow through the separate ProjectSecrets / <see cref="ServiceSpec.Env"/>
/// pipeline.
/// </summary>
[TranspilationSource]
public record RequiredEnvVar
{
    /// <summary>
    /// The env var name, e.g. <c>OPENROUTER_API_KEY</c>. Convention is
    /// <c>^[A-Z][A-Z0-9_]*$</c> (uppercase, underscores) — the standard shell
    /// env var shape. Not templated by the expander; the key is literal.
    /// </summary>
    [JsonPropertyName("key")]
    public required string Key { get; init; }

    /// <summary>
    /// Optional human-readable explanation rendered next to the field in the
    /// secrets editor (e.g. "API key for the OpenRouter LLM gateway"). May
    /// contain <c>{{handlebars}}</c> placeholders — the expander renders these
    /// against the same template bag used for the rest of the preset.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Hint for the UI's default "is secret" toggle. <c>true</c> means the
    /// value is sensitive (API key, password) and should be masked / stored
    /// as a secret by default; <c>false</c> / null means it's a plain config
    /// value. The operator can always override the toggle in the editor.
    /// </summary>
    [JsonPropertyName("secret")]
    public bool? Secret { get; init; }
}

/// <summary>
/// Health-check definition for a single service. The daemon executes
/// <see cref="Command"/> on the configured interval and reports the result
/// via runtime events.
/// </summary>
[TranspilationSource]
public record HealthcheckSpec
{
    /// <summary>
    /// Shell command whose exit code determines health. Exit 0 = healthy;
    /// non-zero = unhealthy. Required.
    /// </summary>
    [JsonPropertyName("command")]
    public required string Command { get; init; }

    /// <summary>
    /// Poll interval in seconds. Null means "use the daemon default"
    /// (5 seconds). Minimum sensible value is 1; sub-second polling is
    /// intentionally not supported (supervisord polling overhead dominates).
    /// </summary>
    [JsonPropertyName("intervalSeconds")]
    public int? IntervalSeconds { get; init; }
}

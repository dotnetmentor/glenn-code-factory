using System.Text.Json;
using Source.Features.RuntimePresets.Models;

namespace Source.Features.RuntimePresets.Dtos;

/// <summary>
/// Wire-shape projection of <see cref="ServicePreset"/>. Same field set as the
/// entity except <c>EnvTemplate</c> and <c>Parameters</c> are deserialised on
/// the way out (jsonb columns are stored as raw <see cref="string"/> on the
/// entity per the codebase's relational jsonb convention).
///
/// <para><b>Category as string.</b> The entity persists the enum as int (for
/// stable on-disk ordering), but the wire shape exposes the camelCase name so
/// the frontend / agent / curl callers never need a mapping table. The
/// controller maps both directions via <see cref="System.Enum.TryParse{TEnum}(string, bool, out TEnum)"/>
/// with <c>ignoreCase: true</c>.</para>
/// </summary>
public sealed record ServicePresetDto(
    Guid Id,
    string Slug,
    string DisplayName,
    string Description,
    string Category,
    string? IconName,
    bool IsBuiltIn,
    string CommandTemplate,
    Dictionary<string, string> EnvTemplate,
    string? HealthcheckCommand,
    int? HealthcheckInterval,
    string? DefaultUser,
    bool Autorestart,
    string? InstallContribution,
    string? SetupContribution,
    string? InstallVerify,
    List<PresetParameter> Parameters,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>
/// Body shape for <c>POST /api/admin/runtime-presets</c>. Admin clones always
/// land with <c>IsBuiltIn=false</c>; the field is omitted from the wire shape
/// because flipping a row's built-in flag is a migration-only operation.
/// </summary>
public sealed record CreatePresetRequest(
    string Slug,
    string DisplayName,
    string Description,
    string Category,
    string? IconName,
    string CommandTemplate,
    Dictionary<string, string>? EnvTemplate,
    string? HealthcheckCommand,
    int? HealthcheckInterval,
    string? DefaultUser,
    bool Autorestart,
    string? InstallContribution,
    string? SetupContribution,
    string? InstallVerify,
    List<PresetParameter>? Parameters);

/// <summary>
/// Body shape for <c>PUT /api/admin/runtime-presets/{id}</c>. Slug is immutable
/// post-create (it's the agent's tool-schema discriminator and changing it
/// would orphan in-flight proposals), and the built-in flag is migration-only;
/// both are omitted.
/// </summary>
public sealed record UpdatePresetRequest(
    string DisplayName,
    string Description,
    string Category,
    string? IconName,
    string CommandTemplate,
    Dictionary<string, string>? EnvTemplate,
    string? HealthcheckCommand,
    int? HealthcheckInterval,
    string? DefaultUser,
    bool Autorestart,
    string? InstallContribution,
    string? SetupContribution,
    string? InstallVerify,
    List<PresetParameter>? Parameters);

/// <summary>
/// Body shape for <c>POST /api/admin/runtime-presets/{id}/clone</c>. Only the
/// new slug is required; the new display name defaults to "{source.DisplayName} (copy)".
/// </summary>
public sealed record ClonePresetRequest(string NewSlug, string? NewDisplayName);

/// <summary>
/// Body shape for <c>POST /api/admin/runtime-presets/{id}/preview</c>. Same
/// shape as <see cref="Contracts.ServiceInstance.Values"/> — typed JsonElement
/// so numbers / bools / strings round-trip without coercion noise.
/// </summary>
public sealed record PreviewPresetRequest(Dictionary<string, JsonElement>? Values);

/// <summary>
/// Wire shape for the live-preview pane in the admin editor. Partial rendering
/// is allowed — missing required values surface as entries in
/// <see cref="Errors"/> rather than collapsing the whole response into a
/// failure status code, so the operator sees "I haven't set <c>project</c> yet"
/// in the UI instead of a 400.
/// </summary>
public sealed record PreviewPresetResponse(
    string Command,
    Dictionary<string, string> Env,
    string? HealthcheckCommand,
    string? InstallContribution,
    string? SetupContribution,
    string? InstallVerify,
    List<string> Errors);

/// <summary>
/// Wire shape for <c>GET /api/admin/runtime-presets/mise-versions?tool={tool}</c>.
/// Tool name echoed back so the frontend can correlate concurrent dropdowns
/// without tracking the request that triggered each response.
/// </summary>
public sealed record MiseVersionsResponse(string Tool, List<string> Versions);

/// <summary>
/// Wire shape for <c>GET /api/runtime-presets/tool-description</c> — the
/// daemon fetches this at startup to populate the agent's
/// <c>propose_runtime_spec</c> tool description + input schema. JsonElement
/// for the schema so we can serialise raw JSON without dragging a typed
/// JSON-schema record into the contracts.
/// </summary>
public sealed record AgentToolDescriptionResponse(string Description, JsonElement InputSchema);

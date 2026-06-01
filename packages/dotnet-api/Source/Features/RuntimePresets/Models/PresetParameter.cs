using System.Text.Json;
using System.Text.Json.Serialization;

namespace Source.Features.RuntimePresets.Models;

/// <summary>
/// Schema entry for one user-supplied input to a <see cref="ServicePreset"/>.
/// Persisted as one element of <c>ServicePreset.Parameters</c> (a jsonb
/// <c>List&lt;PresetParameter&gt;</c>).
///
/// <para><b>Serialised as JSON, not an EF-owned type.</b> Same convention as
/// the rest of the codebase's jsonb columns — the entity holds a raw string
/// and callers serialise via <see cref="System.Text.Json.JsonSerializer"/>
/// with <see cref="JsonOptions"/> so enums round-trip as readable strings.
/// This keeps EF Core's relational provider untouched and the seed SQL in the
/// migration human-grokable.</para>
/// </summary>
public sealed class PresetParameter
{
    /// <summary>
    /// Placeholder name used inside <c>{{handlebars}}</c> expressions
    /// (e.g. <c>"project"</c>, <c>"dotnetVersion"</c>, <c>"port"</c>).
    /// Required, lowerCamelCase by convention.
    /// </summary>
    public required string Key { get; set; }

    /// <summary>
    /// UI label shown above the form field in the admin / proposal editor
    /// (e.g. <c>"Project path"</c>, <c>".NET version"</c>).
    /// </summary>
    public required string Label { get; set; }

    /// <summary>
    /// Input type the UI renders and the expander validates against
    /// (text / number / checkbox / enum dropdown).
    /// </summary>
    public PresetParameterType Type { get; set; } = PresetParameterType.String;

    /// <summary>
    /// True forces the agent / operator to supply a value; false lets
    /// <see cref="DefaultValue"/> stand in when omitted.
    /// </summary>
    public bool Required { get; set; }

    /// <summary>
    /// Default value used when no caller-supplied value is present. Stored as
    /// string regardless of <see cref="Type"/> — the expander coerces (port
    /// "5338" → integer 5338) at render time.
    /// </summary>
    public string? DefaultValue { get; set; }

    /// <summary>
    /// For <see cref="PresetParameterType.Enum"/> only — the closed set of
    /// allowed values (e.g. <c>["7","8","9"]</c> for <c>dotnetVersion</c>).
    /// Null on non-enum parameters.
    /// </summary>
    public List<string>? EnumOptions { get; set; }

    /// <summary>
    /// Operator-facing helper text rendered under the input
    /// (e.g. <c>"Path under repoDir, e.g. packages/dotnet-api"</c>).
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Optional mise tool name (e.g. <c>"dotnet"</c>, <c>"node"</c>, <c>"python"</c>).
    /// When set, the admin UI shows a "Lookup versions" button that calls
    /// <c>GET /api/admin/runtime-presets/mise-versions?tool={MiseTool}</c> to
    /// populate the version dropdown — saves the operator from memorising
    /// the current list of supported versions.
    /// </summary>
    public string? MiseTool { get; set; }

    /// <summary>
    /// Optional link the UI renders as a "Learn more" affordance — useful
    /// for params like <c>port</c> where the operator wants the relevant
    /// framework's deployment docs.
    /// </summary>
    public string? HelpUrl { get; set; }

    /// <summary>
    /// Shared serializer options for the <c>Parameters</c> jsonb column.
    /// Enums round-trip as strings (so the migration SQL is readable and
    /// reorderings don't shift the on-disk values); property names are
    /// lowerCamelCase to match every other jsonb shape in the codebase.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// Convenience deserializer for the jsonb <c>Parameters</c> column on
    /// <see cref="ServicePreset"/>. Returns an empty list (never null) when the
    /// raw JSON is null / whitespace / a JSON null literal — saves every caller
    /// from re-implementing the same null-guard around
    /// <see cref="JsonSerializer.Deserialize{T}(string, JsonSerializerOptions?)"/>.
    ///
    /// <para>Throws <see cref="JsonException"/> on malformed JSON — that's a
    /// preset-author / migration-seed bug, not a runtime user error, so it
    /// should surface loud rather than silently degrade to an empty list.</para>
    /// </summary>
    public static List<PresetParameter> DeserializeList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<PresetParameter>();
        }
        return JsonSerializer.Deserialize<List<PresetParameter>>(json, JsonOptions)
               ?? new List<PresetParameter>();
    }
}

/// <summary>
/// Input-type discriminator for a <see cref="PresetParameter"/>. Persisted as
/// the lowerCamelCase enum name inside the jsonb blob, NOT as an int —
/// the migration SQL needs to read like English.
/// </summary>
public enum PresetParameterType
{
    String = 0,
    Integer = 1,
    Boolean = 2,
    Enum = 3,
}

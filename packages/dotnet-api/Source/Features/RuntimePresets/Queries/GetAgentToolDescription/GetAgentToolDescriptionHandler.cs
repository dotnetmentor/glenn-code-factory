using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimePresets.Dtos;
using Source.Features.RuntimePresets.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimePresets.Queries.GetAgentToolDescription;

/// <summary>
/// Handler for <see cref="GetAgentToolDescriptionQuery"/>.
///
/// <para>Reads every live preset from the DB, renders a markdown bullet list
/// of (slug, description, parameter keys) for the LLM-facing description, and
/// constructs a JSON schema where each preset's <c>values</c> shape becomes a
/// branch of a <c>oneOf</c> discriminated on <c>kind</c>. The schema gates
/// out unknown slugs before the proposal hits MediatR, so the only bad shapes
/// that reach the expander are param-level value violations (out-of-enum,
/// non-integer port, etc.).</para>
///
/// <para><b>Why hand-rolled JSON.</b> We don't pull in a JsonSchema.Net or
/// similar dependency — the schema we emit is small, mechanical, and
/// system-internal. Hand-rolled <see cref="JsonElement"/> via a
/// <c>Dictionary&lt;string, object?&gt;</c> tree keeps the dependency surface
/// minimal and the structure obvious from reading the code.</para>
/// </summary>
public sealed class GetAgentToolDescriptionHandler
    : IQueryHandler<GetAgentToolDescriptionQuery, Result<AgentToolDescriptionResponse>>
{
    private readonly ApplicationDbContext _db;

    public GetAgentToolDescriptionHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<AgentToolDescriptionResponse>> Handle(
        GetAgentToolDescriptionQuery request,
        CancellationToken cancellationToken)
    {
        var presets = await _db.ServicePresets
            .AsNoTracking()
            .OrderBy(p => p.Category)
            .ThenBy(p => p.DisplayName)
            .ToListAsync(cancellationToken);

        // Pre-deserialise each preset's parameter schema so we can use it twice
        // (description + schema) without paying the JSON deserialise cost
        // twice.
        var presetParams = presets.ToDictionary(
            p => p.Slug,
            p => PresetParameter.DeserializeList(p.Parameters));

        var description = BuildDescription(presets, presetParams);
        var schema = BuildInputSchema(presets, presetParams);

        // Round-trip the schema dictionary through System.Text.Json so the
        // caller sees a JsonElement (the contract type) rather than a raw
        // Dictionary<string, object?>. Costs one serialise + one parse, but
        // means the wire shape is guaranteed-valid JSON before it leaves the
        // handler.
        var schemaJson = JsonSerializer.Serialize(schema, SchemaJsonOptions);
        using var schemaDoc = JsonDocument.Parse(schemaJson);
        var schemaElement = schemaDoc.RootElement.Clone();

        return Result.Success(new AgentToolDescriptionResponse(description, schemaElement));
    }

    /// <summary>
    /// JsonSerializer options for the schema dictionary. Default web-style:
    /// camelCase property names, null-omitting, indented output disabled
    /// (the daemon parses raw JSON, no human reads this on the wire).
    /// </summary>
    private static readonly JsonSerializerOptions SchemaJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Build the markdown body the LLM sees as its tool description. Lists each
    /// preset with its slug, description and parameter keys, then ends with the
    /// canonical shape and a worked example.
    /// </summary>
    private static string BuildDescription(
        IReadOnlyList<ServicePreset> presets,
        IReadOnlyDictionary<string, List<PresetParameter>> presetParams)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Propose a runtime spec for this project. Pick a preset for each service the project needs:");
        sb.AppendLine();

        foreach (var p in presets)
        {
            var paramKeys = string.Join(", ", presetParams[p.Slug].Select(x => x.Key));
            sb.Append("- \"").Append(p.Slug).Append("\" — ").Append(p.Description);
            if (!string.IsNullOrEmpty(paramKeys))
            {
                sb.Append(" (parameters: ").Append(paramKeys).Append(')');
            }
            sb.AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine("Shape: { proposedSpec: { version: 3, services: [{ kind, name, values }], setup?, install? }, reason }");
        sb.AppendLine();
        sb.AppendLine("WORKED EXAMPLE — this project (.NET + Vite backoffice):");
        sb.AppendLine("{");
        sb.AppendLine("  \"proposedSpec\": {");
        sb.AppendLine("    \"version\": 3,");
        sb.AppendLine("    \"services\": [");
        sb.AppendLine("      { \"kind\": \"dotnet-mise\", \"name\": \"dotnet-api\",");
        sb.AppendLine("        \"values\": { \"project\": \"packages/dotnet-api\", \"dotnetVersion\": \"9\", \"port\": 5338 } },");
        sb.AppendLine("      { \"kind\": \"node-vite\", \"name\": \"backoffice-web\",");
        sb.AppendLine("        \"values\": { \"project\": \"packages/backoffice-web\", \"port\": 5173 } }");
        sb.AppendLine("    ]");
        sb.AppendLine("  },");
        sb.AppendLine("  \"reason\": \"Repo has a .NET API and a Vite frontend; both need a runtime.\"");
        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>
    /// Build the top-level JSON schema for the agent tool input. Discriminates
    /// service variants on <c>kind</c> via <c>oneOf</c>; each branch's
    /// <c>values</c> shape is derived from the preset's parameter schema
    /// (Required → required[], Type → property type, EnumOptions → enum).
    /// </summary>
    private static Dictionary<string, object?> BuildInputSchema(
        IReadOnlyList<ServicePreset> presets,
        IReadOnlyDictionary<string, List<PresetParameter>> presetParams)
    {
        var serviceOneOf = presets
            .Select(p => BuildServiceVariant(p.Slug, presetParams[p.Slug]))
            .Cast<object?>()
            .ToList();

        // Top-level: { proposedSpec, reason }. proposedSpec.services.items is
        // the oneOf above so unknown kinds fail at validate-time.
        return new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["required"] = new List<object?> { "proposedSpec", "reason" },
            ["properties"] = new Dictionary<string, object?>
            {
                ["proposedSpec"] = new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["required"] = new List<object?> { "version", "services" },
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["version"] = new Dictionary<string, object?> { ["const"] = 3 },
                        ["install"] = new Dictionary<string, object?> { ["type"] = "string" },
                        ["installVerify"] = new Dictionary<string, object?> { ["type"] = "string" },
                        ["setup"] = new Dictionary<string, object?> { ["type"] = "string" },
                        ["services"] = new Dictionary<string, object?>
                        {
                            ["type"] = "array",
                            ["minItems"] = 1,
                            ["items"] = new Dictionary<string, object?>
                            {
                                ["oneOf"] = serviceOneOf,
                            },
                        },
                    },
                },
                ["reason"] = new Dictionary<string, object?> { ["type"] = "string" },
            },
        };
    }

    /// <summary>
    /// Build one branch of the <c>services.items.oneOf</c> union — the
    /// per-preset variant shape with <c>kind</c> pinned to the slug via
    /// <c>const</c> and <c>values</c> typed from the parameter schema.
    /// </summary>
    private static Dictionary<string, object?> BuildServiceVariant(
        string slug,
        IReadOnlyList<PresetParameter> parameters)
    {
        var valuesProperties = new Dictionary<string, object?>();
        var requiredValues = new List<object?>();

        foreach (var p in parameters)
        {
            valuesProperties[p.Key] = BuildParameterSchema(p);
            if (p.Required)
            {
                requiredValues.Add(p.Key);
            }
        }

        var valuesSchema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = valuesProperties,
        };
        if (requiredValues.Count > 0)
        {
            valuesSchema["required"] = requiredValues;
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["required"] = new List<object?> { "kind", "name", "values" },
            ["properties"] = new Dictionary<string, object?>
            {
                ["kind"] = new Dictionary<string, object?> { ["const"] = slug },
                ["name"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["values"] = valuesSchema,
            },
        };
    }

    /// <summary>
    /// Translate a single <see cref="PresetParameter"/> into a JSON-schema
    /// property entry. Enum → string + enum[], Integer → integer, Boolean →
    /// boolean, String → string. Description is carried over verbatim so the
    /// LLM sees the same operator-facing helper text.
    /// </summary>
    private static Dictionary<string, object?> BuildParameterSchema(PresetParameter param)
    {
        var entry = new Dictionary<string, object?>();

        switch (param.Type)
        {
            case PresetParameterType.Integer:
                entry["type"] = "integer";
                break;
            case PresetParameterType.Boolean:
                entry["type"] = "boolean";
                break;
            case PresetParameterType.Enum:
                entry["type"] = "string";
                if (param.EnumOptions is { Count: > 0 } opts)
                {
                    entry["enum"] = opts.Cast<object?>().ToList();
                }
                break;
            case PresetParameterType.String:
            default:
                entry["type"] = "string";
                break;
        }

        if (!string.IsNullOrWhiteSpace(param.Description))
        {
            entry["description"] = param.Description;
        }

        return entry;
    }
}

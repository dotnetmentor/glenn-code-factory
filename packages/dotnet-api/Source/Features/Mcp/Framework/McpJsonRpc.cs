using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace Source.Features.Mcp.Framework;

/// <summary>
/// Wire types + helpers for the JSON-RPC 2.0 / MCP Streamable HTTP dispatcher
/// that <see cref="McpControllerBase"/> exposes at the base route of every MCP
/// controller (e.g. <c>POST /api/mcp/kanban/v1</c>).
///
/// <para><b>Why these types are separate from <see cref="McpEnvelope.cs"/>.</b>
/// The <c>McpResponse&lt;T&gt;</c> envelope is the framework's internal,
/// controller-local response shape. The daemon's MCP client doesn't
/// see it — it sees JSON-RPC envelopes. These types only exist on the wire
/// boundary; everything inside the controller still hands back
/// <see cref="McpResponse{T}"/>.</para>
///
/// <para><b>Protocol version.</b> The dispatcher advertises
/// <c>2025-06-18</c> in <see cref="McpInitializeResult.ProtocolVersion"/> —
/// the spec revision the official MCP SDK and the Cursor SDK client both target
/// at the time of this card. A bump in the upstream spec would land here.</para>
/// </summary>
public static class McpJsonRpcConstants
{
    public const string JsonRpcVersion = "2.0";
    public const string ProtocolVersion = "2025-06-18";

    // Standard JSON-RPC error codes.
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    // Application-level error code, used to surface MCP envelope failures
    // back to the SDK without losing the structured payload.
    public const int McpEnvelopeError = -32001;
}

/// <summary>
/// JSON-RPC 2.0 request envelope. Both notification and request shapes share
/// this record; the dispatcher does not currently emit notifications, so
/// <see cref="Id"/> is always present in practice.
/// </summary>
public sealed record McpJsonRpcRequest(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("id")] JsonElement? Id,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("params")] JsonElement? Params);

/// <summary>
/// JSON-RPC 2.0 success response.
/// </summary>
public sealed record McpJsonRpcSuccessResponse(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("id")] JsonElement? Id,
    [property: JsonPropertyName("result")] object Result);

/// <summary>
/// JSON-RPC 2.0 error response. <see cref="Error"/> carries the structured
/// failure; the outer HTTP response is still 200 per the JSON-RPC spec.
/// </summary>
public sealed record McpJsonRpcErrorResponse(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("id")] JsonElement? Id,
    [property: JsonPropertyName("error")] McpJsonRpcError Error);

/// <summary>
/// JSON-RPC 2.0 error object. <see cref="Data"/> holds the original
/// <see cref="McpError"/> when we're surfacing an envelope failure so the
/// daemon (and the model) can see <c>code</c> / <c>retryable</c>.
/// </summary>
public sealed record McpJsonRpcError(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("data")] object? Data);

/// <summary>
/// Response shape for <c>initialize</c> — advertises the protocol version,
/// server identity, and which capability groups this server supports. We
/// claim only <c>tools</c> for now; resources / prompts / sampling come in
/// later cards if we need them.
/// </summary>
public sealed record McpInitializeResult(
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("serverInfo")] McpServerInfo ServerInfo,
    [property: JsonPropertyName("capabilities")] McpCapabilities Capabilities);

public sealed record McpServerInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version);

public sealed record McpCapabilities(
    [property: JsonPropertyName("tools")] McpToolsCapability Tools);

public sealed record McpToolsCapability(
    [property: JsonPropertyName("listChanged")] bool ListChanged);

/// <summary>
/// Response shape for <c>tools/list</c>.
/// </summary>
public sealed record McpToolsListResult(
    [property: JsonPropertyName("tools")] IReadOnlyList<McpToolDescriptor> Tools);

/// <summary>
/// One tool in the catalog. <see cref="InputSchema"/> is a JSON Schema (Draft
/// 2020-12 subset) generated from the C# input record by
/// <see cref="McpJsonSchemaGenerator"/>.
/// </summary>
public sealed record McpToolDescriptor(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("inputSchema")] object InputSchema);

/// <summary>
/// Response shape for <c>tools/call</c>. The MCP convention is a content
/// array of typed blocks; for this dispatcher we always emit a single
/// <c>text</c> block with the serialized result, plus an <c>isError</c>
/// flag the model can branch on without inspecting the content.
/// </summary>
public sealed record McpToolsCallResult(
    [property: JsonPropertyName("content")] IReadOnlyList<McpContentBlock> Content,
    [property: JsonPropertyName("isError")] bool IsError);

public sealed record McpContentBlock(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string Text);

/// <summary>
/// One tool in the framework's per-controller reflection cache. The
/// dispatcher resolves these at first request per controller type and
/// reuses them forever after.
/// </summary>
internal sealed record McpToolEntry(
    string Name,
    string Description,
    Type? InputType,
    MethodInfo Method,
    object InputSchema);

/// <summary>
/// Per-controller tool catalog cache key.
/// </summary>
internal sealed record McpToolCatalog(IReadOnlyDictionary<string, McpToolEntry> ToolsByName)
{
    public IReadOnlyList<McpToolEntry> Tools => ToolsByName.Values.OrderBy(t => t.Name).ToList();
}

/// <summary>
/// Minimal JSON Schema generator for the MCP <c>tools/list</c> response.
///
/// <para><b>Scope.</b> Handles the shapes the existing MCP controllers actually
/// emit: records with string / Guid / int / long / bool / DateTime / enum /
/// nullable / List&lt;T&gt; properties, plus nested records. That's
/// deliberately the floor — MCP clients don't need every JSON Schema
/// keyword, just enough for the model to generate plausible arguments. A
/// fuller generator can land later if we discover a method whose input type
/// doesn't round-trip.</para>
///
/// <para><b>Required fields.</b> Non-nullable reference types and value
/// types without <c>?</c> are reported in <c>required</c>. Nullable property
/// info is read off <see cref="NullabilityInfoContext"/> — the same mechanism
/// the .NET 6+ JSON serializer uses.</para>
/// </summary>
internal static class McpJsonSchemaGenerator
{
    /// <summary>
    /// Build a JSON Schema object for <paramref name="inputType"/>. Returns
    /// an empty <c>{type:"object"}</c> for <see langword="null"/> inputs
    /// (methods that take no body).
    /// </summary>
    public static object BuildSchema(Type? inputType)
    {
        if (inputType is null)
        {
            return new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>(),
            };
        }
        return BuildObjectSchema(inputType);
    }

    private static Dictionary<string, object?> BuildObjectSchema(Type type)
    {
        var properties = new Dictionary<string, object?>();
        var required = new List<string>();
        var nullCtx = new NullabilityInfoContext();

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead) continue;

            var propName = ToJsonPropertyName(prop);
            var nullability = nullCtx.Create(prop);
            var isNullable = nullability.WriteState == NullabilityState.Nullable
                          || Nullable.GetUnderlyingType(prop.PropertyType) is not null;

            properties[propName] = BuildPropertySchema(prop.PropertyType);

            if (!isNullable && IsRequiredForRecord(prop))
            {
                required.Add(propName);
            }
        }

        var schema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties,
        };
        if (required.Count > 0)
        {
            schema["required"] = required;
        }
        return schema;
    }

    private static object BuildPropertySchema(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying == typeof(string))
            return new Dictionary<string, object?> { ["type"] = "string" };

        if (underlying == typeof(Guid))
            return new Dictionary<string, object?> { ["type"] = "string", ["format"] = "uuid" };

        if (underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset))
            return new Dictionary<string, object?> { ["type"] = "string", ["format"] = "date-time" };

        if (underlying == typeof(bool))
            return new Dictionary<string, object?> { ["type"] = "boolean" };

        if (underlying == typeof(int) || underlying == typeof(long)
            || underlying == typeof(short) || underlying == typeof(byte))
            return new Dictionary<string, object?> { ["type"] = "integer" };

        if (underlying == typeof(double) || underlying == typeof(float) || underlying == typeof(decimal))
            return new Dictionary<string, object?> { ["type"] = "number" };

        if (underlying.IsEnum)
        {
            return new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["enum"] = Enum.GetNames(underlying),
            };
        }

        if (underlying.IsArray)
        {
            return new Dictionary<string, object?>
            {
                ["type"] = "array",
                ["items"] = BuildPropertySchema(underlying.GetElementType()!),
            };
        }

        if (underlying.IsGenericType)
        {
            var def = underlying.GetGenericTypeDefinition();
            if (def == typeof(List<>) || def == typeof(IReadOnlyList<>)
                || def == typeof(IList<>) || def == typeof(IEnumerable<>))
            {
                return new Dictionary<string, object?>
                {
                    ["type"] = "array",
                    ["items"] = BuildPropertySchema(underlying.GetGenericArguments()[0]),
                };
            }
        }

        // Nested object — recurse. Cycles aren't expected on MCP input DTOs;
        // a future card adds depth-limit if we discover one.
        return BuildObjectSchema(underlying);
    }

    private static string ToJsonPropertyName(PropertyInfo prop)
    {
        var jsonAttr = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
        if (jsonAttr is not null) return jsonAttr.Name;
        // System.Text.Json default policy is camelCase via JsonSerializerOptions;
        // mirror it here so the generated schema matches the wire payload.
        var name = prop.Name;
        if (string.IsNullOrEmpty(name) || !char.IsUpper(name[0])) return name;
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>
    /// We treat all read/write properties on records as required unless
    /// nullable. Record primary-ctor parameters compile to init-only
    /// properties; <c>init</c>-set is enough to satisfy "can write." If the
    /// MCP author wants a property optional they should mark its type
    /// nullable.
    /// </summary>
    private static bool IsRequiredForRecord(PropertyInfo prop) => prop.CanWrite;
}

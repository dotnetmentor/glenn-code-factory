using System.Text.Json;
using System.Text.Json.Serialization;

namespace Source.Infrastructure.Extensions;

/// <summary>
/// Tolerant <see cref="Guid?"/> JSON converter that maps an empty string
/// (<c>""</c>) to <c>null</c> on read, instead of letting
/// <see cref="System.Text.Json"/>'s built-in <see cref="Guid"/> parser blow up
/// with a <see cref="FormatException"/>.
///
/// <para>Why we need this: the daemon's bootstrap-progress path reuses
/// <c>RuntimeHub.EmitEvent</c> to broadcast status before any
/// <c>AgentSession</c> exists, and it sends <c>sessionId: ""</c> as a
/// "no session yet" sentinel. The default <see cref="Guid"/> reader treats
/// that as a parse error and the SignalR JsonHubProtocol surfaces it as
/// "Parameters to hub method 'EmitEvent' are incorrect", which terminates the
/// daemon connection — wedging every fresh runtime in <c>Bootstrapping</c>.</para>
///
/// <para>Scope: registered on the SignalR JSON protocol only (see
/// <c>ServicesExtensions.AddRealTimeServices</c>); we do not want to soften
/// Guid parsing globally — REST controllers should still reject empty-string
/// guids. The contract on the wire is that <see cref="Guid?"/> + empty-string
/// means "no session" / "bootstrap broadcast"; the hub handler decides what
/// to do with the null.</para>
///
/// <para>Write side: writes <c>null</c> for <c>null</c>, otherwise the
/// canonical Guid string — symmetric with the default behavior so server-to-
/// daemon payloads remain wire-compatible with the existing daemon.</para>
/// </summary>
public sealed class EmptyStringNullableGuidJsonConverter : JsonConverter<Guid?>
{
    public override Guid? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString();
            if (string.IsNullOrEmpty(s))
            {
                // The whole point of this converter: "" → null, no throw.
                return null;
            }

            // Non-empty string: defer to the framework's Guid parser. Reusing
            // Guid.Parse here matches Utf8JsonReader.GetGuid() semantics
            // closely enough for our daemon-emitted payloads (canonical
            // hyphenated form) without us having to re-implement format
            // detection.
            if (Guid.TryParse(s, out var parsed))
            {
                return parsed;
            }

            throw new JsonException($"The JSON value \"{s}\" is not in a supported Guid format.");
        }

        throw new JsonException($"Unexpected token {reader.TokenType} when parsing nullable Guid.");
    }

    public override void Write(Utf8JsonWriter writer, Guid? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(value.Value);
    }
}

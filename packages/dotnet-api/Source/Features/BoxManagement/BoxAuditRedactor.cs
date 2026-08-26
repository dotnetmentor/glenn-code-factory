using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Source.Features.BoxManagement;

/// <summary>
/// Scrubs secrets out of a Box API request body before it is persisted to the
/// <see cref="Models.BoxOperation.RequestPayload"/> audit column. Fork / create /
/// resume bodies carry the runtime's full env contract — including
/// <c>GLENN_RUNTIME_TOKEN</c> (a JWT) and <c>TUNNEL_TOKEN</c> — and the
/// commands-based env refresh embeds the same values inside its shell
/// <c>command</c> string. The audit rows are rendered verbatim in the admin
/// Runtime Monitor drawer, so anything stored here is effectively visible to
/// every SuperAdmin session; keys stay (they're the debugging signal), values
/// are masked.
///
/// <para>Only the REQUEST payload is redacted. <c>ResponsePayload</c> is left
/// untouched because the idempotency replay path returns it verbatim to
/// callers — mutating it would corrupt replayed Box responses.</para>
/// </summary>
public static partial class BoxAuditRedactor
{
    private const string Mask = "***";

    /// <summary>
    /// Env keys whose values are masked when they appear as shell assignments
    /// inside a <c>command</c> string. Matches the heredoc body written by
    /// <c>BoxRuntimeProvisioning.RefreshEnvAndRestartDaemonAsync</c>
    /// (<c>KEY='single-quoted value'</c>) as well as bare <c>KEY=value</c>.
    /// </summary>
    [GeneratedRegex(
        @"(?<key>[A-Z0-9_]*(?:TOKEN|SECRET|PASSWORD|API_KEY|APIKEY)[A-Z0-9_]*)=('(?:[^']|'\\'')*'|\S+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveAssignmentPattern();

    /// <summary>
    /// Anything shaped like a JWT (three base64url segments) gets masked
    /// wherever it appears — belt and braces for payload shapes this class
    /// doesn't know about yet.
    /// </summary>
    [GeneratedRegex(
        @"eyJ[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]{4,}",
        RegexOptions.CultureInvariant)]
    private static partial Regex JwtPattern();

    /// <summary>
    /// Redact a request body destined for the audit trail. JSON-aware: masks
    /// every value in a top-level <c>env</c> object (fork/create/resume bodies)
    /// and sensitive assignments + JWTs inside a top-level <c>command</c> string
    /// (RunBoxCommand bodies). Falls back to pure regex masking when the input
    /// isn't parseable JSON. The output is always valid for the jsonb column
    /// when the input was.
    /// </summary>
    public static string Redact(string? requestPayloadJson)
    {
        if (string.IsNullOrWhiteSpace(requestPayloadJson))
        {
            return "{}";
        }

        try
        {
            var root = JsonNode.Parse(requestPayloadJson);
            if (root is JsonObject obj)
            {
                if (obj["env"] is JsonObject env)
                {
                    foreach (var key in env.Select(p => p.Key).ToList())
                    {
                        env[key] = Mask;
                    }
                }

                if (obj["command"] is JsonValue commandValue
                    && commandValue.TryGetValue<string>(out var command))
                {
                    obj["command"] = RedactText(command);
                }

                return root.ToJsonString(new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
            }
        }
        catch (JsonException)
        {
            // Not JSON — fall through to the text pass below.
        }

        return RedactText(requestPayloadJson);
    }

    private static string RedactText(string text)
    {
        var masked = SensitiveAssignmentPattern().Replace(text, m => $"{m.Groups["key"].Value}='{Mask}'");
        return JwtPattern().Replace(masked, Mask);
    }
}

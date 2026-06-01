using Tapper;

namespace Source.Features.Mcp.Framework;

/// <summary>
/// Wire envelope every MCP method returns over HTTP. Both success and failure
/// responses go out as HTTP 200 with this shape — the failure signal lives in
/// <see cref="Error"/>, not in the HTTP status code. This mirrors the JSON-RPC /
/// MCP convention the daemon's MCP client speaks: HTTP transport errors are for
/// transport failures (network, gateway, auth middleware), application-level
/// failures are part of the envelope.
///
/// <para><b>Exactly one of <see cref="Result"/> / <see cref="Error"/> is
/// populated.</b> Successful calls carry <see cref="Result"/> and a null
/// <see cref="Error"/>; failed calls carry a null <see cref="Result"/> and a
/// populated <see cref="Error"/>. Callers should branch on
/// <c>Error is not null</c>.</para>
///
/// <para><b>[TranspilationSource].</b> The daemon hand-mirrors this shape in TS
/// (mirroring the <see cref="Source.Features.ProjectSecrets.Models.EnvVarDelta"/>
/// convention for hub payloads). Tapper-generating it on the API side keeps the
/// TS shape derivable from the C# without the daemon depending on Swagger.</para>
/// </summary>
[TranspilationSource]
public record McpResponse<T>(T? Result, McpError? Error);

/// <summary>
/// Structured error payload carried inside an <see cref="McpResponse{T}"/>.
///
/// <list type="bullet">
///   <item><see cref="Code"/> — short machine-readable bucket (e.g.
///         <c>"validation_failed"</c>, <c>"not_found"</c>, <c>"internal_error"</c>).
///         Mirrors <see cref="Source.Features.Mcp.Models.McpCall.ErrorCode"/>.</item>
///   <item><see cref="Message"/> — human-readable summary, safe to surface in
///         daemon logs / agent traces. Never includes secrets.</item>
///   <item><see cref="Retryable"/> — whether the daemon's MCP client should
///         consider retrying the call. <c>false</c> for validation / auth /
///         not-found; <c>true</c> for transient infra failures.</item>
///   <item><see cref="Details"/> — optional structured context (e.g. which
///         field failed validation). Free-form bag, kept small.</item>
/// </list>
/// </summary>
[TranspilationSource]
public record McpError(string Code, string Message, bool Retryable, Dictionary<string, object>? Details);

using Tapper;

namespace Source.Features.Mcp.Models;

/// <summary>
/// Outcome buckets for an <see cref="McpCall"/>. Persisted as <c>int</c> so adding
/// new entries later doesn't shift existing rows.
///
/// <list type="bullet">
///   <item><see cref="Success"/> — handler completed and returned a normal response.</item>
///   <item><see cref="ClientError"/> — caller-side problem (bad params, validation
///         failure, missing input).</item>
///   <item><see cref="ServerError"/> — handler-side problem (unhandled exception,
///         downstream dependency failure).</item>
///   <item><see cref="RateLimited"/> — request was rejected because a per-runtime
///         or per-server rate limit was tripped.</item>
///   <item><see cref="Unauthorized"/> — caller's token was invalid / expired.</item>
///   <item><see cref="Forbidden"/> — caller authenticated but lacks scope to the
///         requested MCP method.</item>
/// </list>
/// </summary>
[TranspilationSource]
public enum McpCallStatus
{
    Success = 0,
    ClientError = 1,
    ServerError = 2,
    RateLimited = 3,
    Unauthorized = 4,
    Forbidden = 5,
}

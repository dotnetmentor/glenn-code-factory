namespace Source.Features.Mcp.Models;

/// <summary>
/// Append-only audit row capturing a single MCP call: which runtime made it, which
/// MCP server + method it hit, how long it took, and the outcome. Used for
/// dashboards, rate-limit retros, and abuse forensics.
///
/// <list type="bullet">
///   <item>Immutable. <b>NOT IAuditable</b>, <b>NOT ISoftDelete</b> — the row has
///         only <see cref="CreatedAt"/> as a domain timestamp; we never want a
///         global interceptor stamping audit rows. Mirrors the
///         <see cref="Source.Features.ProjectSecrets.Models.SecretAuditEvent"/> /
///         <c>RuntimeTokenIssue</c> / <c>RuntimeStateEvent</c> pattern.</item>
///   <item>No FK to <c>ProjectRuntime</c> on <see cref="RuntimeId"/> nor to
///         <see cref="McpServer"/> — the audit trail must outlive a hard-delete of
///         either side. Mirrors <c>FlyOperation.RuntimeId</c> /
///         <c>BootstrapRun.RuntimeId</c>. <see cref="ServerName"/> is denormalized
///         from <see cref="McpServer.Name"/> for the same reason — the row has to
///         remain readable even if the MCP server row is later deleted or renamed.</item>
///   <item><b>NEVER store request / response bodies</b> — only sizes. The
///         <see cref="RequestSizeBytes"/> / <see cref="ResponseSizeBytes"/> columns
///         are byte counts the framework records; the bodies themselves don't
///         exist in this row by design (avoids retention / PII / secret-leak risks).</item>
///   <item>Composite indexes on <c>(RuntimeId, CreatedAt DESC)</c> and
///         <c>(ServerName, Method, CreatedAt DESC)</c> — both DESC variants are
///         emitted via raw SQL in the migration since EF Core 9 doesn't expose
///         per-column sort order on relational indexes (matches the
///         <c>FlyOperation</c> / <c>BootstrapRun</c> / <c>SecretAuditEvent</c>
///         precedent).</item>
/// </list>
/// </summary>
public class McpCall
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Runtime that issued the call. Plain Guid (no FK) — the audit row must
    /// outlive any future runtime hard-delete.
    /// </summary>
    public Guid RuntimeId { get; set; }

    /// <summary>
    /// Denormalized <see cref="McpServer.Name"/> for outlive-the-row coherence.
    /// Capped at 100 chars to mirror the source column.
    /// </summary>
    public string ServerName { get; set; } = string.Empty;

    /// <summary>MCP method name (e.g. <c>"tools/call"</c>, <c>"resources/read"</c>). Capped at 100 chars.</summary>
    public string Method { get; set; } = string.Empty;

    /// <summary>Wall-clock duration of the call in milliseconds.</summary>
    public int DurationMs { get; set; }

    /// <summary>Outcome bucket. See <see cref="McpCallStatus"/>.</summary>
    public McpCallStatus Status { get; set; }

    /// <summary>
    /// Optional structured error code when <see cref="Status"/> is non-success
    /// (e.g. <c>"validation/missing_field"</c>). Capped at 100 chars; null on success.
    /// </summary>
    public string? ErrorCode { get; set; }

    /// <summary>Size of the inbound request payload in bytes. Body itself never stored.</summary>
    public int RequestSizeBytes { get; set; }

    /// <summary>Size of the outbound response payload in bytes. Body itself never stored.</summary>
    public int ResponseSizeBytes { get; set; }

    /// <summary>UTC timestamp when the call completed. Set by the framework; never an interceptor.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

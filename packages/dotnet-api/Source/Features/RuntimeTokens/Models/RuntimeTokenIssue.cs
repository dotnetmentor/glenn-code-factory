namespace Source.Features.RuntimeTokens.Models;

/// <summary>
/// Append-only audit row written every time main API mints a RuntimeToken JWT.
/// One row per issued token; a revoke updates <see cref="RevokedAt"/> in place but
/// the row itself is never deleted (the spec calls out "Loss of a token never
/// means loss of audit").
///
/// <para>The <see cref="Id"/> column doubles as the JWT <c>jti</c> claim — that's
/// how the validation path looks up "is this token revoked?" without needing a
/// secondary index. <see cref="TokenHash"/> is sha256-hex of the full issued JWT
/// string and is purely forensic: if a token shows up in a leak channel, ops can
/// match the leak against this column without ever needing the original token.</para>
///
/// <para><b>NOT IAuditable, NOT ISoftDelete.</b> Mirrors the
/// <see cref="Source.Features.RuntimeLifecycle.Models.RuntimeStateEvent"/> pattern.
/// IssuedAt and RevokedAt are domain timestamps the service sets explicitly; we
/// never want a global interceptor stamping them.</para>
/// </summary>
public class RuntimeTokenIssue
{
    /// <summary>JWT jti claim. PK.</summary>
    public Guid Id { get; set; }

    public Guid RuntimeId { get; set; }

    /// <summary>Mirrors ProjectRuntime.TenantId nullability — pre-tenancy runtimes have null.</summary>
    public Guid? TenantId { get; set; }

    public Guid ProjectId { get; set; }

    /// <summary>Optional — main branch tokens may not carry a branch claim.</summary>
    public Guid? BranchId { get; set; }

    /// <summary>Token scope, e.g. "runtime". Free-form string today; could become an enum later.</summary>
    public string Scope { get; set; } = "runtime";

    /// <summary>SHA-256 hex of the issued JWT. Forensic-only; never the JWT itself.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTime IssuedAt { get; set; }
    public DateTime ExpiresAt { get; set; }

    public DateTime? RevokedAt { get; set; }
    public string? RevocationReason { get; set; }

    /// <summary>
    /// Last UTC instant this token was successfully validated. Updated by the
    /// batched flush in <c>RuntimeTokenUsageRecorder</c> every 30 seconds —
    /// the validate hot path NEVER touches the database. Null until the first
    /// validation lands.
    /// </summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>
    /// Cumulative count of successful validations for this token. Updated by
    /// the batched flush in <c>RuntimeTokenUsageRecorder</c> via an additive
    /// increment. <c>long</c> because a busy daemon at 1Hz pings produces
    /// ~31M validations a year — well within bigint, comfortably above int.
    /// </summary>
    public long RequestCount { get; set; }
}

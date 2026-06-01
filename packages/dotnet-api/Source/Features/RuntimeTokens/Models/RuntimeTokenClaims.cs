namespace Source.Features.RuntimeTokens.Models;

/// <summary>
/// Strongly-typed projection of the JWT claims a RuntimeToken carries. The wire
/// claim names live in <see cref="RuntimeTokenClaimNames"/> so producers and
/// consumers agree without string-soup.
/// </summary>
public record RuntimeTokenClaims(
    Guid Jti,
    Guid RuntimeId,
    Guid ProjectId,
    Guid? BranchId,
    Guid? TenantId,
    string Scope,
    DateTime IssuedAt,
    DateTime ExpiresAt);

/// <summary>
/// Wire-level claim names. Kept ASCII-short and prefix-namespaced (`rt_`) so they
/// don't collide with standard JWT claims (`sub`, `iss`, `aud`, `exp`, `iat`,
/// `jti`) — those we read from the standard slots, these we read from custom
/// slots.
/// </summary>
public static class RuntimeTokenClaimNames
{
    public const string RuntimeId = "rt_runtime";
    public const string ProjectId = "rt_project";
    public const string BranchId  = "rt_branch";
    public const string TenantId  = "rt_tenant";
    public const string Scope     = "rt_scope";
}

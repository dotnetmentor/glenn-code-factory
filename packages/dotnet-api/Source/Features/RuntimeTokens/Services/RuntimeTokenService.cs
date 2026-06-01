using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Source.Features.RuntimeTokens.Models;
using Source.Infrastructure;
using Source.Shared.Results;

namespace Source.Features.RuntimeTokens.Services;

/// <summary>
/// Caller-facing request to mint a new RuntimeToken JWT. <see cref="Lifetime"/>
/// being null falls through to the service default (7 days) — explicit is
/// always allowed and overrides the default.
/// </summary>
public record MintTokenRequest(
    Guid RuntimeId,
    Guid ProjectId,
    Guid? BranchId,
    Guid? TenantId,
    string Scope = "runtime",
    TimeSpan? Lifetime = null);

/// <summary>
/// What we hand back to the caller after a successful mint. The token itself is
/// returned only after the corresponding <see cref="Models.RuntimeTokenIssue"/>
/// audit row has been persisted — a failed audit write means the caller never
/// sees the token.
/// </summary>
public record MintTokenResult(
    string Token,
    Guid Jti,
    DateTime IssuedAt,
    DateTime ExpiresAt);

/// <summary>
/// Mints and validates RuntimeToken JWTs. Signing material comes from
/// <see cref="IRuntimeTokenSigningKeyService"/>; audit rows are written to
/// <see cref="ApplicationDbContext.RuntimeTokenIssues"/>; revoked-jti lookups
/// hit the in-memory <see cref="IRevocationCache"/> on every validate.
/// </summary>
public interface IRuntimeTokenService
{
    /// <summary>
    /// Mint a new RuntimeToken JWT. Fails (without writing an audit row) if
    /// <see cref="MintTokenRequest.TenantId"/> is null — every runtime created
    /// via the project-scoped provisioning flow has a non-null TenantId
    /// (sourced from <c>Project.WorkspaceId</c>), so a null here indicates a
    /// malformed runtime row and we refuse to mint a tenant-less token rather
    /// than silently break tenancy isolation.
    /// </summary>
    Task<Result<MintTokenResult>> MintAsync(MintTokenRequest req, CancellationToken ct = default);
    Task<Result<RuntimeTokenClaims>> ValidateAsync(string token, CancellationToken ct = default);
}

public class RuntimeTokenService : IRuntimeTokenService
{
    // Constants — never read from appsettings.json. Issuer/audience are part of
    // the protocol contract between main API and runtimes; changing them is a
    // code change, not a config change.
    public const string Issuer = "glenn-main-api";
    public const string Audience = "glenn-runtime";
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(7);

    private readonly IRuntimeTokenSigningKeyService _signingKeyService;
    private readonly ApplicationDbContext _db;
    private readonly IRevocationCache _revocationCache;
    private readonly ILogger<RuntimeTokenService> _logger;

    public RuntimeTokenService(
        IRuntimeTokenSigningKeyService signingKeyService,
        ApplicationDbContext db,
        IRevocationCache revocationCache,
        ILogger<RuntimeTokenService> logger)
    {
        _signingKeyService = signingKeyService;
        _db = db;
        _revocationCache = revocationCache;
        _logger = logger;
    }

    public async Task<Result<MintTokenResult>> MintAsync(MintTokenRequest req, CancellationToken ct = default)
    {
        // Tenancy chain: Project.WorkspaceId -> ProjectRuntime.TenantId -> JWT
        // rt_tenant claim. Card 3 of the e2e-smoketest spec made TenantId
        // non-null on every runtime created through the live provisioning path.
        // A null here means either a malformed legacy row or a caller that
        // wasn't migrated — either way, minting a token without rt_tenant
        // would silently defeat tenancy isolation downstream. Refuse, audit
        // nothing, and let the caller surface the error.
        if (!req.TenantId.HasValue)
        {
            return Result.Failure<MintTokenResult>(
                "Cannot mint runtime token: ProjectRuntime.TenantId is null. " +
                "This indicates a malformed runtime row.");
        }

        var jti = Guid.NewGuid();
        var iat = DateTime.UtcNow;
        var exp = iat + (req.Lifetime ?? DefaultLifetime);

        var handler = new JwtSecurityTokenHandler();
        // Suppress the default inbound/outbound claim-name remapping. We control
        // claim names ourselves (rt_*) and don't want JwtSecurityTokenHandler
        // rewriting them to its preferred SOAP-style URIs.
        handler.OutboundClaimTypeMap.Clear();

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Jti, jti.ToString()),
            new(RuntimeTokenClaimNames.RuntimeId, req.RuntimeId.ToString()),
            new(RuntimeTokenClaimNames.ProjectId, req.ProjectId.ToString()),
            new(RuntimeTokenClaimNames.TenantId, req.TenantId.Value.ToString()),
            new(RuntimeTokenClaimNames.Scope, req.Scope),
        };
        if (req.BranchId.HasValue)
            claims.Add(new Claim(RuntimeTokenClaimNames.BranchId, req.BranchId.Value.ToString()));

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            // SecurityTokenDescriptor.Subject controls the rt_* custom claims; the
            // standard slots (iss/aud/iat/exp/nbf/jti) come from the dedicated props.
            Subject = new ClaimsIdentity(claims),
            IssuedAt = iat,
            NotBefore = iat,
            Expires = exp,
            SigningCredentials = _signingKeyService.GetCurrentSigning(),
        };

        var securityToken = handler.CreateToken(descriptor);
        // The SigningKeyService stamps each key's KeyId with its SystemSettings slot
        // name ("RuntimeTokens:SigningKeyCurrent" / "...Previous"). If we leave the
        // resulting `kid` header in the JWT, validation matches by slot name —
        // which breaks rotation: the *new* Current key carries the same `kid` as
        // the old one did, so kid-matching resolves to the wrong key for tokens
        // minted before the rotation. Strip the `kid` header so validation falls
        // back to "try every key in IssuerSigningKeys" (TryAllIssuerSigningKeys).
        if (securityToken is JwtSecurityToken jwt)
        {
            jwt.Header.Remove("kid");
        }
        var token = handler.WriteToken(securityToken);

        // sha256-hex of the issued JWT — purely forensic, never the JWT itself.
        // Length must be 64 (matches the migration's HasMaxLength(64) on TokenHash).
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        var tokenHash = Convert.ToHexString(hashBytes).ToLowerInvariant();

        var issue = new Models.RuntimeTokenIssue
        {
            Id = jti,
            RuntimeId = req.RuntimeId,
            ProjectId = req.ProjectId,
            BranchId = req.BranchId,
            TenantId = req.TenantId,
            Scope = req.Scope,
            TokenHash = tokenHash,
            IssuedAt = iat,
            ExpiresAt = exp,
            RevokedAt = null,
        };

        _db.RuntimeTokenIssues.Add(issue);
        // Audit before issuance: if this throws, the caller never receives the token.
        await _db.SaveChangesAsync(ct);

        return Result.Success(new MintTokenResult(token, jti, iat, exp));
    }

    public Task<Result<RuntimeTokenClaims>> ValidateAsync(string token, CancellationToken ct = default)
    {
        var handler = new JwtSecurityTokenHandler();
        handler.InboundClaimTypeMap.Clear();

        var parameters = new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidateIssuer = true,
            ValidAudience = Audience,
            ValidateAudience = true,
            IssuerSigningKeys = _signingKeyService.GetValidationKeys(),
            ValidateIssuerSigningKey = true,
            // The default kid-based resolver shortcuts to the matching KeyId; during
            // rotation the *new* Current carries the same KeyId as the *old* Current
            // did, so the kid match resolves to the wrong key for tokens signed
            // before the rotation. Falling through to "try every key" lets the
            // previous key actually be tried and succeed.
            TryAllIssuerSigningKeys = true,
            ValidateLifetime = true,
            // 30s skew tolerance — typical practice. Clients on slightly drifting
            // clocks shouldn't see spurious expiry/notbefore failures; anything
            // beyond ~30s and we *want* to reject because something's wrong.
            ClockSkew = TimeSpan.FromSeconds(30),
        };

        ClaimsPrincipal principal;
        SecurityToken validated;
        try
        {
            principal = handler.ValidateToken(token, parameters, out validated);
        }
        catch (SecurityTokenExpiredException)
        {
            return Task.FromResult(Result.Failure<RuntimeTokenClaims>("token_expired"));
        }
        catch (Exception ex)
        {
            // Log at Debug — failed validation is expected operational noise; we
            // never log the token itself, only the exception type/message. Don't
            // surface the inner reason to callers; "token_invalid" is the contract.
            _logger.LogDebug(ex, "RuntimeToken validation failed");
            return Task.FromResult(Result.Failure<RuntimeTokenClaims>("token_invalid"));
        }

        if (validated is not JwtSecurityToken jwt)
            return Task.FromResult(Result.Failure<RuntimeTokenClaims>("token_invalid"));

        var jtiStr = principal.FindFirstValue(JwtRegisteredClaimNames.Jti);
        var runtimeStr = principal.FindFirstValue(RuntimeTokenClaimNames.RuntimeId);
        var projectStr = principal.FindFirstValue(RuntimeTokenClaimNames.ProjectId);
        var scope = principal.FindFirstValue(RuntimeTokenClaimNames.Scope);

        if (!Guid.TryParse(jtiStr, out var jti)
            || !Guid.TryParse(runtimeStr, out var runtimeId)
            || !Guid.TryParse(projectStr, out var projectId)
            || string.IsNullOrEmpty(scope))
        {
            return Task.FromResult(Result.Failure<RuntimeTokenClaims>("token_invalid"));
        }

        Guid? branchId = null;
        var branchStr = principal.FindFirstValue(RuntimeTokenClaimNames.BranchId);
        if (!string.IsNullOrEmpty(branchStr))
        {
            if (!Guid.TryParse(branchStr, out var parsedBranch))
                return Task.FromResult(Result.Failure<RuntimeTokenClaims>("token_invalid"));
            branchId = parsedBranch;
        }

        Guid? tenantId = null;
        var tenantStr = principal.FindFirstValue(RuntimeTokenClaimNames.TenantId);
        if (!string.IsNullOrEmpty(tenantStr))
        {
            if (!Guid.TryParse(tenantStr, out var parsedTenant))
                return Task.FromResult(Result.Failure<RuntimeTokenClaims>("token_invalid"));
            tenantId = parsedTenant;
        }

        // JwtSecurityTokenHandler writes iat/exp as Unix seconds — read back via
        // DateTimeOffset.FromUnixTimeSeconds(...).UtcDateTime to round-trip cleanly.
        var iatClaim = principal.FindFirstValue(JwtRegisteredClaimNames.Iat);
        var expClaim = principal.FindFirstValue(JwtRegisteredClaimNames.Exp);
        if (!long.TryParse(iatClaim, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iatSeconds)
            || !long.TryParse(expClaim, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expSeconds))
        {
            return Task.FromResult(Result.Failure<RuntimeTokenClaims>("token_invalid"));
        }
        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(iatSeconds).UtcDateTime;
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(expSeconds).UtcDateTime;

        // Signature/lifetime/issuer/audience were all verified above, so jti is
        // attacker-controlled-but-trusted at this point — safe to use as a cache
        // key. Cache check happens AFTER full JWT validation so we never trust an
        // unsigned jti, and BEFORE returning success so a revoke takes effect on
        // the very next validate.
        if (_revocationCache.IsRevoked(jti))
        {
            return Task.FromResult(Result.Failure<RuntimeTokenClaims>("token_revoked"));
        }

        return Task.FromResult(Result.Success(new RuntimeTokenClaims(
            jti, runtimeId, projectId, branchId, tenantId, scope, issuedAt, expiresAt)));
    }
}

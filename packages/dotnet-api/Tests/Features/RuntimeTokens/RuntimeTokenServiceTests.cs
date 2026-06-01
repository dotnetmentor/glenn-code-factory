using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Source.Features.RuntimeTokens.EventHandlers;
using Source.Features.RuntimeTokens.Models;
using Source.Features.RuntimeTokens.Services;
using Source.Features.SystemSettings.Events;
using Source.Features.SystemSettings.Services;
using Source.Infrastructure;

namespace Api.Tests.Features.RuntimeTokens;

/// <summary>
/// Round-trip and edge-case coverage for <see cref="RuntimeTokenService"/>:
/// mint persists exactly one audit row, validation accepts well-formed tokens
/// (and tokens signed with the previous key during rotation), and rejects
/// expired / tampered / wrong-issuer tokens with the right error codes.
///
/// <para>We don't mock <see cref="IRuntimeTokenSigningKeyService"/> — the real
/// implementation is wired through an in-memory <see cref="ApplicationDbContext"/>
/// (same shape as <see cref="RuntimeTokenSigningKeyServiceTests"/>) so we
/// exercise the full mint-to-validate path.</para>
/// </summary>
public class RuntimeTokenServiceTests : IDisposable
{
    private readonly string _dbName;
    private readonly ServiceProvider _sp;
    private readonly RuntimeTokenSigningKeyService _signingKeyService;
    private readonly ApplicationDbContext _ctx;
    private readonly RuntimeTokenService _sut;

    public RuntimeTokenServiceTests()
    {
        _dbName = Guid.NewGuid().ToString();
        var cipherKeyB64 = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new SystemSettingsCipherOptions { EncryptionKey = cipherKeyB64 }));
        services.AddSingleton<ISystemSettingsCipher, SystemSettingsCipher>();
        services.AddSingleton<SystemSettingsCache>();
        services.AddScoped(_ => TestDbContextFactory.Create(_dbName));
        services.AddScoped<ISystemSettingsService, SystemSettingsService>();
        _sp = services.BuildServiceProvider();

        _signingKeyService = new RuntimeTokenSigningKeyService(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RuntimeTokenSigningKeyService>.Instance);

        _ctx = TestDbContextFactory.Create(_dbName);
        // No-op revocation cache — these tests don't exercise revoke flow; cache
        // wiring is covered in RevocationCacheTests. Mock.Of<> returns a stub
        // where IsRevoked(_) returns false by default.
        _sut = new RuntimeTokenService(
            _signingKeyService,
            _ctx,
            Mock.Of<IRevocationCache>(),
            NullLogger<RuntimeTokenService>.Instance);
    }

    private ApplicationDbContext OpenDb() => TestDbContextFactory.Create(_dbName);

    public void Dispose()
    {
        _ctx.Dispose();
        _sp.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Mint_then_Validate_round_trips_all_claims()
    {
        var req = new MintTokenRequest(
            RuntimeId: Guid.NewGuid(),
            ProjectId: Guid.NewGuid(),
            BranchId: Guid.NewGuid(),
            TenantId: Guid.NewGuid(),
            Scope: "runtime",
            Lifetime: TimeSpan.FromHours(1));

        var mintResult = await _sut.MintAsync(req);
        mintResult.IsSuccess.Should().BeTrue($"mint must succeed (error={mintResult.Error})");
        var minted = mintResult.Value;
        var result = await _sut.ValidateAsync(minted.Token);

        result.IsSuccess.Should().BeTrue();
        var claims = result.Value;
        claims.Jti.Should().Be(minted.Jti);
        claims.RuntimeId.Should().Be(req.RuntimeId);
        claims.ProjectId.Should().Be(req.ProjectId);
        claims.BranchId.Should().Be(req.BranchId);
        claims.TenantId.Should().Be(req.TenantId);
        claims.Scope.Should().Be("runtime");
        // JWT iat/exp serialize as Unix seconds; allow ~1s skew on round-trip.
        claims.IssuedAt.Should().BeCloseTo(minted.IssuedAt, TimeSpan.FromSeconds(1));
        claims.ExpiresAt.Should().BeCloseTo(minted.ExpiresAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Mint_with_null_BranchId_validates_and_projects_BranchId_as_null()
    {
        // Card 4 of e2e-smoketest tightened the contract: TenantId is now
        // required. BranchId, however, remains optional — we still ship a
        // single-branch-per-runtime daemon today, so the mint path must keep
        // accepting null BranchId without complaint.
        var req = new MintTokenRequest(
            RuntimeId: Guid.NewGuid(),
            ProjectId: Guid.NewGuid(),
            BranchId: null,
            TenantId: Guid.NewGuid());

        var mintResult = await _sut.MintAsync(req);
        mintResult.IsSuccess.Should().BeTrue($"mint must succeed with null BranchId (error={mintResult.Error})");
        var minted = mintResult.Value;
        var result = await _sut.ValidateAsync(minted.Token);

        result.IsSuccess.Should().BeTrue("missing rt_branch must NOT throw");
        result.Value.BranchId.Should().BeNull();
        result.Value.TenantId.Should().Be(req.TenantId);
        result.Value.RuntimeId.Should().Be(req.RuntimeId);
        result.Value.ProjectId.Should().Be(req.ProjectId);
    }

    [Fact]
    public async Task Mint_with_null_TenantId_returns_failure_and_writes_no_audit_row()
    {
        // Tenancy-chain enforcement (Card 4 of e2e-smoketest):
        // Project.WorkspaceId -> ProjectRuntime.TenantId -> JWT rt_tenant.
        // Refusing to mint without a TenantId is the load-bearing piece — a
        // tenant-less JWT silently defeats isolation downstream.
        long auditBefore;
        await using (var pre = OpenDb())
        {
            auditBefore = await pre.RuntimeTokenIssues.LongCountAsync();
        }

        var mintResult = await _sut.MintAsync(new MintTokenRequest(
            RuntimeId: Guid.NewGuid(),
            ProjectId: Guid.NewGuid(),
            BranchId: null,
            TenantId: null));

        mintResult.IsFailure.Should().BeTrue("null TenantId must be refused");
        mintResult.Error.Should().Contain("TenantId is null",
            "the failure message should make the malformed runtime row obvious to the operator");

        await using var post = OpenDb();
        var auditAfter = await post.RuntimeTokenIssues.LongCountAsync();
        auditAfter.Should().Be(auditBefore,
            "a refused mint must not leave an issuance audit row behind");
    }

    [Fact]
    public async Task Mint_writes_rt_tenant_claim_into_jwt_payload()
    {
        // Direct decode of the JWT payload (no validation indirection) to prove
        // the rt_tenant slot is populated from MintTokenRequest.TenantId. This
        // guards against a regression where someone reorders the claim list and
        // accidentally drops rt_tenant — Validate's projection alone wouldn't
        // catch that because the Validate path is symmetric with Mint.
        var tenantId = Guid.NewGuid();
        var mintResult = await _sut.MintAsync(new MintTokenRequest(
            RuntimeId: Guid.NewGuid(),
            ProjectId: Guid.NewGuid(),
            BranchId: null,
            TenantId: tenantId,
            Lifetime: TimeSpan.FromHours(1)));

        mintResult.IsSuccess.Should().BeTrue($"mint must succeed (error={mintResult.Error})");

        var jwt = new JwtSecurityToken(mintResult.Value.Token);
        var tenantClaim = jwt.Claims.SingleOrDefault(c => c.Type == RuntimeTokenClaimNames.TenantId);
        tenantClaim.Should().NotBeNull("rt_tenant must be present in the JWT payload");
        tenantClaim!.Value.Should().Be(tenantId.ToString(),
            "rt_tenant value must equal the runtime's TenantId stringified");
    }

    [Fact]
    public async Task Tampered_signature_returns_token_invalid()
    {
        var mintResult = await _sut.MintAsync(new MintTokenRequest(
            RuntimeId: Guid.NewGuid(),
            ProjectId: Guid.NewGuid(),
            BranchId: null,
            TenantId: Guid.NewGuid()));
        mintResult.IsSuccess.Should().BeTrue($"setup mint must succeed (error={mintResult.Error})");
        var minted = mintResult.Value;

        // JWT structure is header.payload.signature — mutate one base64 char in the
        // signature segment so HMAC verification fails.
        var parts = minted.Token.Split('.');
        parts.Should().HaveCount(3);
        var sigChars = parts[2].ToCharArray();
        sigChars[0] = sigChars[0] == 'A' ? 'B' : 'A';
        var tampered = $"{parts[0]}.{parts[1]}.{new string(sigChars)}";

        var result = await _sut.ValidateAsync(tampered);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("token_invalid");
    }

    [Fact]
    public async Task Expired_token_returns_token_expired()
    {
        // Hand-craft a JWT in the past so it's already expired (well outside the
        // 30s clock-skew window). Going through the service is awkward because
        // its current contract assumes Lifetime > 0 — JWT itself rejects exp <= nbf
        // at write time. The service's lifetime path is exercised in the round-trip
        // test; this test exists to verify the *Validate* error code.
        var creds = _signingKeyService.GetCurrentSigning();
        var handler = new JwtSecurityTokenHandler();
        handler.OutboundClaimTypeMap.Clear();

        var iat = DateTime.UtcNow.AddHours(-2);
        var exp = DateTime.UtcNow.AddHours(-1); // expired an hour ago
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = RuntimeTokenService.Issuer,
            Audience = RuntimeTokenService.Audience,
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                new Claim(RuntimeTokenClaimNames.RuntimeId, Guid.NewGuid().ToString()),
                new Claim(RuntimeTokenClaimNames.ProjectId, Guid.NewGuid().ToString()),
                new Claim(RuntimeTokenClaimNames.Scope, "runtime"),
            }),
            IssuedAt = iat,
            NotBefore = iat,
            Expires = exp,
            SigningCredentials = creds,
        };
        var expired = handler.WriteToken(handler.CreateToken(descriptor));

        var result = await _sut.ValidateAsync(expired);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("token_expired");
    }

    [Fact]
    public async Task Wrong_issuer_returns_token_invalid()
    {
        // Hand-craft a JWT signed with the SAME key but with iss = "evil".
        // ValidateToken throws SecurityTokenInvalidIssuerException -> token_invalid.
        var creds = _signingKeyService.GetCurrentSigning();
        var handler = new JwtSecurityTokenHandler();
        handler.OutboundClaimTypeMap.Clear();

        var jti = Guid.NewGuid();
        var iat = DateTime.UtcNow;
        var exp = iat.AddHours(1);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = "evil",
            Audience = RuntimeTokenService.Audience,
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(JwtRegisteredClaimNames.Jti, jti.ToString()),
                new Claim(RuntimeTokenClaimNames.RuntimeId, Guid.NewGuid().ToString()),
                new Claim(RuntimeTokenClaimNames.ProjectId, Guid.NewGuid().ToString()),
                new Claim(RuntimeTokenClaimNames.Scope, "runtime"),
            }),
            IssuedAt = iat,
            NotBefore = iat,
            Expires = exp,
            SigningCredentials = creds,
        };
        var bad = handler.WriteToken(handler.CreateToken(descriptor));

        var result = await _sut.ValidateAsync(bad);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("token_invalid");
    }

    [Fact]
    public async Task Mint_persists_exactly_one_RuntimeTokenIssue_row()
    {
        long before;
        await using (var pre = OpenDb())
        {
            before = await pre.RuntimeTokenIssues.LongCountAsync();
        }

        var req = new MintTokenRequest(
            RuntimeId: Guid.NewGuid(),
            ProjectId: Guid.NewGuid(),
            BranchId: Guid.NewGuid(),
            TenantId: Guid.NewGuid(),
            Lifetime: TimeSpan.FromMinutes(30));

        var mintResult = await _sut.MintAsync(req);
        mintResult.IsSuccess.Should().BeTrue($"mint must succeed (error={mintResult.Error})");
        var minted = mintResult.Value;

        await using var post = OpenDb();
        var after = await post.RuntimeTokenIssues.LongCountAsync();
        (after - before).Should().Be(1);

        var row = await post.RuntimeTokenIssues.SingleAsync(r => r.Id == minted.Jti);
        row.Id.Should().Be(minted.Jti);
        row.RuntimeId.Should().Be(req.RuntimeId);
        row.ProjectId.Should().Be(req.ProjectId);
        row.BranchId.Should().Be(req.BranchId);
        row.TenantId.Should().Be(req.TenantId);
        row.Scope.Should().Be("runtime");
        row.TokenHash.Length.Should().Be(64, "sha256 hex must be exactly 64 chars");
        row.TokenHash.Should().MatchRegex("^[0-9a-f]{64}$", "lowercase hex only");
        (row.ExpiresAt - row.IssuedAt).Should().BeCloseTo(TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(1));
        row.RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task TokenHash_in_audit_row_equals_sha256_hex_of_returned_token()
    {
        var mintResult = await _sut.MintAsync(new MintTokenRequest(
            RuntimeId: Guid.NewGuid(),
            ProjectId: Guid.NewGuid(),
            BranchId: null,
            TenantId: Guid.NewGuid()));
        mintResult.IsSuccess.Should().BeTrue($"mint must succeed (error={mintResult.Error})");
        var minted = mintResult.Value;

        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(minted.Token)))
            .ToLowerInvariant();

        await using var db = OpenDb();
        var row = await db.RuntimeTokenIssues.SingleAsync(r => r.Id == minted.Jti);
        row.TokenHash.Should().Be(expected);
    }

    [Fact]
    public async Task Validation_accepts_token_signed_with_previous_key_after_rotation()
    {
        // 1. Mint a token with the auto-seeded Current key.
        var mintResult = await _sut.MintAsync(new MintTokenRequest(
            RuntimeId: Guid.NewGuid(),
            ProjectId: Guid.NewGuid(),
            BranchId: null,
            TenantId: Guid.NewGuid(),
            Lifetime: TimeSpan.FromHours(1)));
        mintResult.IsSuccess.Should().BeTrue($"setup mint must succeed (error={mintResult.Error})");
        var minted = mintResult.Value;

        // 2. Capture what the signing-key service believes is "Current" right now —
        //    that becomes "Previous" post-rotation.
        var originalCurrentBytes = ((SymmetricSecurityKey)_signingKeyService.GetCurrentSigning().Key).Key;

        // 3. Operator rotates: move Current -> Previous, set a fresh Current.
        var freshCurrentB64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var oldCurrentB64 = Convert.ToBase64String(originalCurrentBytes);
        using (var scope = _sp.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<ISystemSettingsService>();
            await settings.SetAsync(RuntimeTokenSigningKeyService.PreviousKeyName, oldCurrentB64, isSecret: true, updatedBy: "test:rotate");
            await settings.SetAsync(RuntimeTokenSigningKeyService.CurrentKeyName, freshCurrentB64, isSecret: true, updatedBy: "test:rotate");
        }

        // 4. Drop the cache (event handler does this in production).
        var invalidator = new RuntimeTokenSigningKeyCacheInvalidator(
            _signingKeyService,
            NullLogger<RuntimeTokenSigningKeyCacheInvalidator>.Instance);
        await invalidator.Handle(
            new SystemSettingChanged(RuntimeTokenSigningKeyService.CurrentKeyName, RuntimeTokenSigningKeyService.Category),
            CancellationToken.None);

        // 5. Sanity: validation set now contains both keys; the fresh Current is
        //    different from the old one we minted with.
        var keys = _signingKeyService.GetValidationKeys();
        keys.Should().HaveCount(2);
        ((SymmetricSecurityKey)_signingKeyService.GetCurrentSigning().Key).Key
            .Should().NotEqual(originalCurrentBytes);

        // 6. The token minted under the OLD key must still validate — that's the
        //    rotation contract.
        var result = await _sut.ValidateAsync(minted.Token);
        result.IsSuccess.Should().BeTrue(
            $"a token signed with the previous key must validate during the rotation window (error={result.Error})");
        result.Value.Jti.Should().Be(minted.Jti);
    }
}

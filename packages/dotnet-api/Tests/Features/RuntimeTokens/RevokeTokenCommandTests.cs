using Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Source.Features.RuntimeTokens.Commands;
using Source.Features.RuntimeTokens.Models;
using Source.Features.RuntimeTokens.Services;
using Source.Features.SystemSettings.Services;
using Source.Infrastructure;

namespace Api.Tests.Features.RuntimeTokens;

/// <summary>
/// Coverage for <see cref="RevokeTokenCommandHandler"/>: validation, idempotency,
/// already-expired no-op, reason truncation, and the cache-prime side effect on
/// success. The "happy path" test uses the real <see cref="RuntimeTokenService"/>
/// + real <see cref="RevocationCache"/> so we verify that ValidateAsync reflects
/// the revoke — i.e. the slice end-to-end. The other tests use a mock cache so
/// we can assert exactly when Revoke(...) is called.
/// </summary>
public class RevokeTokenCommandTests : IDisposable
{
    private readonly string _dbName;
    private readonly ApplicationDbContext _ctx;

    public RevokeTokenCommandTests()
    {
        _dbName = Guid.NewGuid().ToString();
        _ctx = TestDbContextFactory.Create(_dbName);
    }

    public void Dispose()
    {
        _ctx.Dispose();
        GC.SuppressFinalize(this);
    }

    private ApplicationDbContext OpenDb() => TestDbContextFactory.Create(_dbName);

    private static RuntimeTokenIssue NewIssue(
        Guid? id = null,
        Guid? runtimeId = null,
        Guid? tenantId = null,
        DateTime? expiresAt = null,
        DateTime? revokedAt = null,
        string? revocationReason = null)
    {
        var iat = DateTime.UtcNow.AddMinutes(-1);
        return new RuntimeTokenIssue
        {
            Id = id ?? Guid.NewGuid(),
            RuntimeId = runtimeId ?? Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            TenantId = tenantId,
            BranchId = null,
            Scope = "runtime",
            TokenHash = new string('a', 64),
            IssuedAt = iat,
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddHours(1),
            RevokedAt = revokedAt,
            RevocationReason = revocationReason,
        };
    }

    [Fact]
    public async Task Token_not_found_returns_token_not_found_failure()
    {
        var handler = new RevokeTokenCommandHandler(_ctx, Mock.Of<IRevocationCache>());

        var result = await handler.Handle(
            new RevokeTokenCommand(Guid.NewGuid(), "leaked"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("token_not_found");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_or_whitespace_reason_fails_with_revocation_reason_required(string reason)
    {
        // Seed an actual row so we know the failure is reason-validation, not a
        // missing-row short-circuit.
        var issue = NewIssue();
        _ctx.RuntimeTokenIssues.Add(issue);
        await _ctx.SaveChangesAsync();

        var handler = new RevokeTokenCommandHandler(_ctx, Mock.Of<IRevocationCache>());

        var result = await handler.Handle(
            new RevokeTokenCommand(issue.Id, reason),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("revocation_reason_required");
    }

    [Fact]
    public async Task Happy_path_revokes_row_primes_cache_and_blocks_subsequent_validation()
    {
        // Build a real-token round-trip stack, same shape as RuntimeTokenServiceTests.
        var cipherKeyB64 = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new SystemSettingsCipherOptions { EncryptionKey = cipherKeyB64 }));
        services.AddSingleton<ISystemSettingsCipher, SystemSettingsCipher>();
        services.AddSingleton<SystemSettingsCache>();
        services.AddScoped(_ => TestDbContextFactory.Create(_dbName));
        services.AddScoped<ISystemSettingsService, SystemSettingsService>();
        using var sp = services.BuildServiceProvider();

        var signingKeys = new RuntimeTokenSigningKeyService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RuntimeTokenSigningKeyService>.Instance);

        var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = new RevocationCache(
            memory,
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RevocationCache>.Instance);

        using var serviceDb = OpenDb();
        var tokenService = new RuntimeTokenService(
            signingKeys, serviceDb, cache, NullLogger<RuntimeTokenService>.Instance);

        var mintResult = await tokenService.MintAsync(new MintTokenRequest(
            RuntimeId: Guid.NewGuid(),
            ProjectId: Guid.NewGuid(),
            BranchId: null,
            TenantId: Guid.NewGuid(),
            Lifetime: TimeSpan.FromHours(1)));
        mintResult.IsSuccess.Should().BeTrue($"setup mint must succeed (error={mintResult.Error})");
        var minted = mintResult.Value;

        // Sanity: pre-revoke validation succeeds.
        var before = await tokenService.ValidateAsync(minted.Token);
        before.IsSuccess.Should().BeTrue();

        // Run the handler against a fresh DbContext (matches how MediatR provides one per request).
        using var handlerDb = OpenDb();
        var handler = new RevokeTokenCommandHandler(handlerDb, cache);
        var result = await handler.Handle(
            new RevokeTokenCommand(minted.Jti, "leaked"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        // DB row updated.
        await using var verifyDb = OpenDb();
        var row = await verifyDb.RuntimeTokenIssues.SingleAsync(r => r.Id == minted.Jti);
        row.RevokedAt.Should().NotBeNull();
        row.RevocationReason.Should().Be("leaked");

        // Cache primed.
        cache.IsRevoked(minted.Jti).Should().BeTrue();

        // Validation now reports token_revoked.
        var after = await tokenService.ValidateAsync(minted.Token);
        after.IsFailure.Should().BeTrue();
        after.Error.Should().Be("token_revoked");
    }

    [Fact]
    public async Task Idempotent_second_revoke_keeps_first_RevokedAt_and_reason_unchanged()
    {
        var issue = NewIssue();
        _ctx.RuntimeTokenIssues.Add(issue);
        await _ctx.SaveChangesAsync();

        var cacheMock = new Mock<IRevocationCache>();

        var firstHandler = new RevokeTokenCommandHandler(OpenDb(), cacheMock.Object);
        var firstResult = await firstHandler.Handle(
            new RevokeTokenCommand(issue.Id, "first"),
            CancellationToken.None);
        firstResult.IsSuccess.Should().BeTrue();

        DateTime firstRevokedAt;
        string firstReason;
        await using (var db1 = OpenDb())
        {
            var row1 = await db1.RuntimeTokenIssues.SingleAsync(r => r.Id == issue.Id);
            row1.RevokedAt.Should().NotBeNull();
            firstRevokedAt = row1.RevokedAt!.Value;
            firstReason = row1.RevocationReason!;
        }

        // Sleep so any (incorrect) overwrite would produce a measurably different timestamp.
        Thread.Sleep(50);

        var secondHandler = new RevokeTokenCommandHandler(OpenDb(), cacheMock.Object);
        var secondResult = await secondHandler.Handle(
            new RevokeTokenCommand(issue.Id, "second-different-reason"),
            CancellationToken.None);
        secondResult.IsSuccess.Should().BeTrue("idempotent revoke must report success");

        await using var verifyDb = OpenDb();
        var row = await verifyDb.RuntimeTokenIssues.SingleAsync(r => r.Id == issue.Id);
        row.RevokedAt.Should().Be(firstRevokedAt, "first revocation wins for the audit trail");
        row.RevocationReason.Should().Be(firstReason, "first reason is preserved; second is ignored");

        // Cache.Revoke is called exactly once — only on the first revocation.
        cacheMock.Verify(c => c.Revoke(issue.Id, It.IsAny<DateTime>()), Times.Once);
    }

    [Fact]
    public async Task Already_expired_row_is_unchanged_and_returns_success()
    {
        var issue = NewIssue(expiresAt: DateTime.UtcNow.AddHours(-1));
        _ctx.RuntimeTokenIssues.Add(issue);
        await _ctx.SaveChangesAsync();

        var cacheMock = new Mock<IRevocationCache>();
        var handler = new RevokeTokenCommandHandler(OpenDb(), cacheMock.Object);

        var result = await handler.Handle(
            new RevokeTokenCommand(issue.Id, "test"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        // Read from a fresh context to be sure no in-memory tracker is hiding stale state.
        await using var verifyDb = OpenDb();
        var row = await verifyDb.RuntimeTokenIssues.SingleAsync(r => r.Id == issue.Id);
        row.RevokedAt.Should().BeNull("already-expired tokens are no-ops; we don't write anything");
        row.RevocationReason.Should().BeNull();

        // Cache must NOT be primed for an already-expired token.
        cacheMock.Verify(c => c.Revoke(It.IsAny<Guid>(), It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public async Task Reason_longer_than_256_chars_is_truncated_to_256()
    {
        var issue = NewIssue();
        _ctx.RuntimeTokenIssues.Add(issue);
        await _ctx.SaveChangesAsync();

        var longReason = new string('x', 1000);
        var handler = new RevokeTokenCommandHandler(OpenDb(), Mock.Of<IRevocationCache>());

        var result = await handler.Handle(
            new RevokeTokenCommand(issue.Id, longReason),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await using var verifyDb = OpenDb();
        var row = await verifyDb.RuntimeTokenIssues.SingleAsync(r => r.Id == issue.Id);
        row.RevocationReason.Should().NotBeNull();
        row.RevocationReason!.Length.Should().Be(256, "RevocationReason has HasMaxLength(256); over-long is silently clamped");
    }
}

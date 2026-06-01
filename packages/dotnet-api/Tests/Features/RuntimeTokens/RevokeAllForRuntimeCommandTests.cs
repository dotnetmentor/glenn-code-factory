using Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Source.Features.RuntimeTokens.Commands;
using Source.Features.RuntimeTokens.Models;
using Source.Features.RuntimeTokens.Services;
using Source.Infrastructure;

namespace Api.Tests.Features.RuntimeTokens;

/// <summary>
/// Coverage for <see cref="RevokeAllForRuntimeCommandHandler"/>: filters by
/// runtime, returns the count of newly-revoked rows, ignores rows that were
/// already revoked or already expired, primes the cache exactly for the rows
/// the handler itself updated.
/// </summary>
public class RevokeAllForRuntimeCommandTests : IDisposable
{
    private readonly string _dbName;
    private readonly ApplicationDbContext _ctx;

    public RevokeAllForRuntimeCommandTests()
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
        Guid runtimeId,
        DateTime? expiresAt = null,
        DateTime? revokedAt = null,
        string? revocationReason = null,
        Guid? tenantId = null)
    {
        var iat = DateTime.UtcNow.AddMinutes(-5);
        return new RuntimeTokenIssue
        {
            Id = Guid.NewGuid(),
            RuntimeId = runtimeId,
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
    public async Task Returns_count_of_rows_revoked_and_leaves_pre_revoked_row_alone()
    {
        var runtimeId = Guid.NewGuid();
        var alive1 = NewIssue(runtimeId);
        var alive2 = NewIssue(runtimeId);
        var preRevokedAt = DateTime.UtcNow.AddMinutes(-30);
        var alreadyRevoked = NewIssue(runtimeId, revokedAt: preRevokedAt, revocationReason: "earlier");

        _ctx.RuntimeTokenIssues.AddRange(alive1, alive2, alreadyRevoked);
        await _ctx.SaveChangesAsync();

        var cacheMock = new Mock<IRevocationCache>();
        var handler = new RevokeAllForRuntimeCommandHandler(OpenDb(), cacheMock.Object);

        var result = await handler.Handle(
            new RevokeAllForRuntimeCommand(runtimeId, "tenant-rotation"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(2);

        await using var verifyDb = OpenDb();
        var rows = await verifyDb.RuntimeTokenIssues
            .Where(r => r.RuntimeId == runtimeId)
            .ToListAsync();

        rows.Single(r => r.Id == alive1.Id).RevokedAt.Should().NotBeNull();
        rows.Single(r => r.Id == alive1.Id).RevocationReason.Should().Be("tenant-rotation");
        rows.Single(r => r.Id == alive2.Id).RevokedAt.Should().NotBeNull();
        rows.Single(r => r.Id == alive2.Id).RevocationReason.Should().Be("tenant-rotation");

        // Pre-revoked row is untouched: original RevokedAt + reason preserved.
        var preRow = rows.Single(r => r.Id == alreadyRevoked.Id);
        preRow.RevokedAt.Should().BeCloseTo(preRevokedAt, TimeSpan.FromSeconds(1));
        preRow.RevocationReason.Should().Be("earlier");

        // Cache primed for the two newly-revoked jtis only — NOT for the pre-revoked one.
        cacheMock.Verify(c => c.Revoke(alive1.Id, It.IsAny<DateTime>()), Times.Once);
        cacheMock.Verify(c => c.Revoke(alive2.Id, It.IsAny<DateTime>()), Times.Once);
        cacheMock.Verify(c => c.Revoke(alreadyRevoked.Id, It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public async Task Different_runtimes_are_ignored()
    {
        var runtimeA = Guid.NewGuid();
        var runtimeB = Guid.NewGuid();
        var aIssue = NewIssue(runtimeA);
        var bIssue = NewIssue(runtimeB);

        _ctx.RuntimeTokenIssues.AddRange(aIssue, bIssue);
        await _ctx.SaveChangesAsync();

        var handler = new RevokeAllForRuntimeCommandHandler(OpenDb(), Mock.Of<IRevocationCache>());

        var result = await handler.Handle(
            new RevokeAllForRuntimeCommand(runtimeA, "test"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(1);

        await using var verifyDb = OpenDb();
        var aRow = await verifyDb.RuntimeTokenIssues.SingleAsync(r => r.Id == aIssue.Id);
        var bRow = await verifyDb.RuntimeTokenIssues.SingleAsync(r => r.Id == bIssue.Id);

        aRow.RevokedAt.Should().NotBeNull();
        bRow.RevokedAt.Should().BeNull("runtime B's tokens must not be touched when revoking runtime A");
    }

    [Fact]
    public async Task Empty_match_returns_success_with_count_zero()
    {
        var handler = new RevokeAllForRuntimeCommandHandler(OpenDb(), Mock.Of<IRevocationCache>());

        var result = await handler.Handle(
            new RevokeAllForRuntimeCommand(Guid.NewGuid(), "test"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(0);
    }

    [Fact]
    public async Task Cache_primed_for_each_newly_revoked_jti_only()
    {
        var runtimeId = Guid.NewGuid();
        var alive1 = NewIssue(runtimeId);
        var alive2 = NewIssue(runtimeId);
        // Already-revoked row — its cache state is none of this handler's business.
        var preRevoked = NewIssue(
            runtimeId,
            revokedAt: DateTime.UtcNow.AddMinutes(-1),
            revocationReason: "earlier");

        _ctx.RuntimeTokenIssues.AddRange(alive1, alive2, preRevoked);
        await _ctx.SaveChangesAsync();

        // Use a real RevocationCache so we can observe IsRevoked() rather than
        // mock-verify call counts.
        var memory = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var cache = new RevocationCache(
            memory,
            scopeFactory: new InertScopeFactory(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RevocationCache>.Instance);

        var handler = new RevokeAllForRuntimeCommandHandler(OpenDb(), cache);

        var result = await handler.Handle(
            new RevokeAllForRuntimeCommand(runtimeId, "test"),
            CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(2);

        cache.IsRevoked(alive1.Id).Should().BeTrue();
        cache.IsRevoked(alive2.Id).Should().BeTrue();
        // The pre-revoked row was NOT primed by the handler (we never inserted it
        // into the cache in setup either) — handler must not redundantly re-prime it.
        cache.IsRevoked(preRevoked.Id).Should().BeFalse();
    }

    [Fact]
    public async Task Already_expired_rows_are_skipped_in_count_and_in_DB_writes()
    {
        var runtimeId = Guid.NewGuid();
        var expired = NewIssue(runtimeId, expiresAt: DateTime.UtcNow.AddHours(-1));
        var alive = NewIssue(runtimeId);

        _ctx.RuntimeTokenIssues.AddRange(expired, alive);
        await _ctx.SaveChangesAsync();

        var cacheMock = new Mock<IRevocationCache>();
        var handler = new RevokeAllForRuntimeCommandHandler(OpenDb(), cacheMock.Object);

        var result = await handler.Handle(
            new RevokeAllForRuntimeCommand(runtimeId, "test"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(1);

        await using var verifyDb = OpenDb();
        var expiredRow = await verifyDb.RuntimeTokenIssues.SingleAsync(r => r.Id == expired.Id);
        expiredRow.RevokedAt.Should().BeNull("already-expired rows are filtered out — JWT lifetime check rejects them anyway");
    }

    [Fact]
    public async Task Empty_reason_fails_with_revocation_reason_required()
    {
        var handler = new RevokeAllForRuntimeCommandHandler(OpenDb(), Mock.Of<IRevocationCache>());

        var result = await handler.Handle(
            new RevokeAllForRuntimeCommand(Guid.NewGuid(), "   "),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("revocation_reason_required");
    }

    /// <summary>
    /// Stand-in for IServiceScopeFactory: the warm-from-DB path is never hit in
    /// these tests, so a real factory isn't needed. Throws to make any
    /// inadvertent reach to WarmFromDatabaseAsync loud.
    /// </summary>
    private sealed class InertScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() =>
            throw new NotImplementedException("Test reached WarmFromDatabaseAsync unexpectedly.");
    }
}

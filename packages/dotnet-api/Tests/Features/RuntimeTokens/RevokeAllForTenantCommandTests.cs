using Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Moq;
using Source.Features.RuntimeTokens.Commands;
using Source.Features.RuntimeTokens.Models;
using Source.Features.RuntimeTokens.Services;
using Source.Infrastructure;

namespace Api.Tests.Features.RuntimeTokens;

/// <summary>
/// Coverage for <see cref="RevokeAllForTenantCommandHandler"/>: filters by
/// TenantId column (no Tenant entity exists today), null-tenant rows are NOT
/// touched, already-revoked rows are excluded from the count.
/// </summary>
public class RevokeAllForTenantCommandTests : IDisposable
{
    private readonly string _dbName;
    private readonly ApplicationDbContext _ctx;

    public RevokeAllForTenantCommandTests()
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
        Guid? tenantId = null,
        Guid? runtimeId = null,
        DateTime? expiresAt = null,
        DateTime? revokedAt = null,
        string? revocationReason = null)
    {
        var iat = DateTime.UtcNow.AddMinutes(-5);
        return new RuntimeTokenIssue
        {
            Id = Guid.NewGuid(),
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
    public async Task Filters_by_TenantId_and_leaves_other_tenants_alone()
    {
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();

        // Tenant T1: two runtimes, each with one alive token.
        var t1RuntimeA = NewIssue(tenantId: t1);
        var t1RuntimeB = NewIssue(tenantId: t1);

        // Tenant T2: one runtime with one alive token — must NOT be touched.
        var t2Issue = NewIssue(tenantId: t2);

        _ctx.RuntimeTokenIssues.AddRange(t1RuntimeA, t1RuntimeB, t2Issue);
        await _ctx.SaveChangesAsync();

        var handler = new RevokeAllForTenantCommandHandler(OpenDb(), Mock.Of<IRevocationCache>());

        var result = await handler.Handle(
            new RevokeAllForTenantCommand(t1, "tenant-shutdown"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(2);

        await using var verifyDb = OpenDb();
        var t1A = await verifyDb.RuntimeTokenIssues.SingleAsync(r => r.Id == t1RuntimeA.Id);
        var t1B = await verifyDb.RuntimeTokenIssues.SingleAsync(r => r.Id == t1RuntimeB.Id);
        var t2Row = await verifyDb.RuntimeTokenIssues.SingleAsync(r => r.Id == t2Issue.Id);

        t1A.RevokedAt.Should().NotBeNull();
        t1A.RevocationReason.Should().Be("tenant-shutdown");
        t1B.RevokedAt.Should().NotBeNull();
        t1B.RevocationReason.Should().Be("tenant-shutdown");

        t2Row.RevokedAt.Should().BeNull("tenant T2's tokens must not be touched when revoking tenant T1");
        t2Row.RevocationReason.Should().BeNull();
    }

    [Fact]
    public async Task Null_tenant_rows_are_not_touched()
    {
        // Pre-tenancy runtime: TenantId = null.
        var nullTenantIssue = NewIssue(tenantId: null);
        _ctx.RuntimeTokenIssues.Add(nullTenantIssue);
        await _ctx.SaveChangesAsync();

        var handler = new RevokeAllForTenantCommandHandler(OpenDb(), Mock.Of<IRevocationCache>());

        // Pass a non-null Guid — the null-tenant row must NOT match.
        var result = await handler.Handle(
            new RevokeAllForTenantCommand(Guid.NewGuid(), "test"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(0);

        await using var verifyDb = OpenDb();
        var row = await verifyDb.RuntimeTokenIssues.SingleAsync(r => r.Id == nullTenantIssue.Id);
        row.RevokedAt.Should().BeNull("null TenantId never matches a Guid filter");
    }

    [Fact]
    public async Task Already_revoked_rows_are_excluded_from_count()
    {
        var t1 = Guid.NewGuid();
        var alive = NewIssue(tenantId: t1);
        var preRevoked = NewIssue(
            tenantId: t1,
            revokedAt: DateTime.UtcNow.AddMinutes(-30),
            revocationReason: "earlier");

        _ctx.RuntimeTokenIssues.AddRange(alive, preRevoked);
        await _ctx.SaveChangesAsync();

        var handler = new RevokeAllForTenantCommandHandler(OpenDb(), Mock.Of<IRevocationCache>());

        var result = await handler.Handle(
            new RevokeAllForTenantCommand(t1, "test"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(1, "the already-revoked row is excluded from the count and the update");

        await using var verifyDb = OpenDb();
        var aliveRow = await verifyDb.RuntimeTokenIssues.SingleAsync(r => r.Id == alive.Id);
        var preRow = await verifyDb.RuntimeTokenIssues.SingleAsync(r => r.Id == preRevoked.Id);

        aliveRow.RevokedAt.Should().NotBeNull();
        aliveRow.RevocationReason.Should().Be("test");

        preRow.RevocationReason.Should().Be("earlier", "first revocation wins; second pass leaves it alone");
    }

    [Fact]
    public async Task Empty_reason_fails_with_revocation_reason_required()
    {
        var handler = new RevokeAllForTenantCommandHandler(OpenDb(), Mock.Of<IRevocationCache>());

        var result = await handler.Handle(
            new RevokeAllForTenantCommand(Guid.NewGuid(), ""),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("revocation_reason_required");
    }
}

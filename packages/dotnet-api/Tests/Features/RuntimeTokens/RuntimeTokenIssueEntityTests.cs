using Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeTokens.Models;

namespace Api.Tests.Features.RuntimeTokens;

/// <summary>
/// Smoke tests for the <see cref="RuntimeTokenIssue"/> entity. We don't exercise
/// any service behaviour here — the mint/validate/rotate/revoke handlers arrive
/// in follow-up cards. We just verify the EF model is wired up correctly:
///
/// <list type="bullet">
///   <item>Round-trip persistence works for all columns.</item>
///   <item>The (RuntimeId) and (ExpiresAt, RevokedAt) indexes are usable for
///         their intended queries — runtime-scoped audit and rotation scan.</item>
///   <item>Revoking a row in a second SaveChanges populates RevokedAt /
///         RevocationReason in place; the row is never deleted.</item>
///   <item>The migration file lands with the right name and contains the
///         expected three CreateIndex calls. Mirrors the
///         <c>ProjectRuntimeEntityTests.State_enum_is_persisted_as_string</c>
///         pattern of asserting against the generated migration text.</item>
/// </list>
/// </summary>
public class RuntimeTokenIssueEntityTests : HandlerTestBase
{
    [Fact]
    public async Task Can_round_trip_RuntimeTokenIssue()
    {
        var issuedAt = DateTime.UtcNow;
        var issue = new RuntimeTokenIssue
        {
            Id = Guid.NewGuid(),
            RuntimeId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            BranchId = Guid.NewGuid(),
            Scope = "runtime",
            TokenHash = new string('a', 64),
            IssuedAt = issuedAt,
            ExpiresAt = issuedAt.AddHours(1),
        };

        Context.RuntimeTokenIssues.Add(issue);
        await Context.SaveChangesAsync();

        // Force a reload from the in-memory store so we don't read tracked instance.
        Context.ChangeTracker.Clear();

        var reloaded = await Context.RuntimeTokenIssues.SingleAsync(r => r.Id == issue.Id);

        reloaded.Id.Should().Be(issue.Id);
        reloaded.RuntimeId.Should().Be(issue.RuntimeId);
        reloaded.TenantId.Should().Be(issue.TenantId);
        reloaded.ProjectId.Should().Be(issue.ProjectId);
        reloaded.BranchId.Should().Be(issue.BranchId);
        reloaded.Scope.Should().Be("runtime");
        reloaded.TokenHash.Should().Be(issue.TokenHash);
        reloaded.IssuedAt.Should().Be(issue.IssuedAt);
        reloaded.ExpiresAt.Should().Be(issue.ExpiresAt);
        reloaded.RevokedAt.Should().BeNull();
        reloaded.RevocationReason.Should().BeNull();
    }

    [Fact]
    public async Task Can_find_issues_by_RuntimeId()
    {
        var runtimeA = Guid.NewGuid();
        var runtimeB = Guid.NewGuid();
        var now = DateTime.UtcNow;

        Context.RuntimeTokenIssues.AddRange(
            new RuntimeTokenIssue
            {
                Id = Guid.NewGuid(),
                RuntimeId = runtimeA,
                ProjectId = Guid.NewGuid(),
                TokenHash = new string('a', 64),
                IssuedAt = now,
                ExpiresAt = now.AddHours(1),
            },
            new RuntimeTokenIssue
            {
                Id = Guid.NewGuid(),
                RuntimeId = runtimeB,
                ProjectId = Guid.NewGuid(),
                TokenHash = new string('b', 64),
                IssuedAt = now,
                ExpiresAt = now.AddHours(1),
            });

        await Context.SaveChangesAsync();
        Context.ChangeTracker.Clear();

        var forRuntimeA = await Context.RuntimeTokenIssues
            .Where(r => r.RuntimeId == runtimeA)
            .ToListAsync();

        forRuntimeA.Should().HaveCount(1);
        forRuntimeA.Single().RuntimeId.Should().Be(runtimeA);
    }

    [Fact]
    public async Task Rotation_scan_returns_only_non_revoked_tokens_nearing_expiry()
    {
        var now = DateTime.UtcNow;

        var nearExpiryActive = new RuntimeTokenIssue
        {
            Id = Guid.NewGuid(),
            RuntimeId = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            TokenHash = new string('a', 64),
            IssuedAt = now,
            ExpiresAt = now.AddHours(1),
        };
        var nearExpiryRevoked = new RuntimeTokenIssue
        {
            Id = Guid.NewGuid(),
            RuntimeId = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            TokenHash = new string('b', 64),
            IssuedAt = now,
            ExpiresAt = now.AddHours(1),
            RevokedAt = now,
            RevocationReason = "test",
        };
        var farFromExpiryActive = new RuntimeTokenIssue
        {
            Id = Guid.NewGuid(),
            RuntimeId = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            TokenHash = new string('c', 64),
            IssuedAt = now,
            ExpiresAt = now.AddDays(30),
        };

        Context.RuntimeTokenIssues.AddRange(nearExpiryActive, nearExpiryRevoked, farFromExpiryActive);
        await Context.SaveChangesAsync();
        Context.ChangeTracker.Clear();

        var threshold = now.AddDays(1);
        var dueForRotation = await Context.RuntimeTokenIssues
            .Where(r => r.ExpiresAt < threshold && r.RevokedAt == null)
            .ToListAsync();

        dueForRotation.Should().HaveCount(1);
        dueForRotation.Single().Id.Should().Be(nearExpiryActive.Id);
    }

    [Fact]
    public async Task Revocation_updates_existing_row_in_place()
    {
        var now = DateTime.UtcNow;
        var issue = new RuntimeTokenIssue
        {
            Id = Guid.NewGuid(),
            RuntimeId = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            TokenHash = new string('a', 64),
            IssuedAt = now,
            ExpiresAt = now.AddHours(1),
        };
        Context.RuntimeTokenIssues.Add(issue);
        await Context.SaveChangesAsync();

        // Second SaveChanges flips revocation fields.
        var revokedAt = now.AddMinutes(5);
        issue.RevokedAt = revokedAt;
        issue.RevocationReason = "leaked";
        await Context.SaveChangesAsync();

        Context.ChangeTracker.Clear();
        var reloaded = await Context.RuntimeTokenIssues.SingleAsync(r => r.Id == issue.Id);

        reloaded.RevokedAt.Should().Be(revokedAt);
        reloaded.RevocationReason.Should().Be("leaked");
    }

    [Fact]
    public void Migration_file_exists_with_expected_indexes()
    {
        // Pattern borrowed from ProjectRuntimeEntityTests.State_enum_is_persisted_as_string —
        // walk up from the test binary's BaseDirectory to find the Migrations folder.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Migrations")))
        {
            dir = dir.Parent;
        }
        dir.Should().NotBeNull("could not locate the Migrations directory from the test binary");

        var migrationsPath = Path.Combine(dir!.FullName, "Migrations");
        var migrationFiles = Directory.GetFiles(migrationsPath, "*_AddRuntimeTokenIssue.cs");
        migrationFiles.Should().NotBeEmpty("a migration file ending in '_AddRuntimeTokenIssue.cs' must exist");

        var content = File.ReadAllText(migrationFiles.Single());

        // The three indexes the entity configuration declares — assert they all
        // made it through to the generated migration.
        content.Should().Contain("IX_RuntimeTokenIssues_RuntimeId",
            "RuntimeId index is required for runtime-scoped audit lookups");
        content.Should().Contain("IX_RuntimeTokenIssues_TenantId",
            "TenantId index is required for tenant-scoped audit lookups");
        content.Should().Contain("IX_RuntimeTokenIssues_ExpiresAt_RevokedAt",
            "Composite (ExpiresAt, RevokedAt) index is required for the rotation scan");
    }
}

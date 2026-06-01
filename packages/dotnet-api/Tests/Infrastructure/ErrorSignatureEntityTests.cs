using Microsoft.EntityFrameworkCore;
using Source.Features.ErrorLog.Models;

namespace Api.Tests.Infrastructure;

/// <summary>
/// Tests for the <see cref="ErrorSignature"/> entity and its integration with the
/// existing <see cref="Source.Features.ErrorLog.Models.ErrorLog"/> entity.
///
/// Notes:
/// - Uses the in-memory EF provider via <see cref="HandlerTestBase"/>.
/// - Unique-index enforcement is verified through <i>model metadata</i>, because
///   the in-memory provider does not actually enforce unique indexes at runtime.
///   (See <see cref="ErrorSignature_Hash_IsUniqueIndexed"/>.)
/// </summary>
public class ErrorSignatureEntityTests : HandlerTestBase
{
    [Fact]
    public async Task ErrorSignature_RoundTrips()
    {
        var now = DateTime.UtcNow;
        var signature = new ErrorSignature
        {
            Id = Guid.NewGuid(),
            Hash = new string('a', 64),
            Source = "HTTP",
            Severity = "Error",
            FirstSeenAt = now.AddHours(-1),
            LastSeenAt = now,
            Count = 42,
            IsResolved = false,
            ResolvedAt = null,
            CreatedAt = now.AddHours(-1),
            UpdatedAt = now,
        };

        Context.Set<ErrorSignature>().Add(signature);
        await Context.SaveChangesAsync();

        // New context to force a reload from the in-memory store
        Context.ChangeTracker.Clear();

        var reloaded = await Context.Set<ErrorSignature>().FirstAsync(s => s.Id == signature.Id);

        reloaded.Hash.Should().Be(signature.Hash);
        reloaded.Source.Should().Be("HTTP");
        reloaded.Severity.Should().Be("Error");
        reloaded.Count.Should().Be(42);
        reloaded.IsResolved.Should().BeFalse();
        reloaded.ResolvedAt.Should().BeNull();
        reloaded.FirstSeenAt.Should().BeCloseTo(signature.FirstSeenAt, TimeSpan.FromSeconds(1));
        reloaded.LastSeenAt.Should().BeCloseTo(signature.LastSeenAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ErrorSignature_Hash_IsUniqueIndexed()
    {
        // In-memory EF does NOT enforce unique indexes, so verify via model metadata instead.
        var entityType = Context.Model.FindEntityType(typeof(ErrorSignature));
        entityType.Should().NotBeNull();

        var hashIndex = entityType!
            .GetIndexes()
            .FirstOrDefault(i => i.Properties.Count == 1 && i.Properties[0].Name == nameof(ErrorSignature.Hash));

        hashIndex.Should().NotBeNull("there must be an index on ErrorSignature.Hash");
        hashIndex!.IsUnique.Should().BeTrue("the index on Hash must be unique");
    }

    [Fact]
    public async Task ErrorLog_SignatureFk_NavigatesToParent()
    {
        var now = DateTime.UtcNow;
        var signature = new ErrorSignature
        {
            Id = Guid.NewGuid(),
            Hash = new string('b', 64),
            Source = "HTTP",
            Severity = "Error",
            FirstSeenAt = now,
            LastSeenAt = now,
            Count = 1,
        };
        Context.Set<ErrorSignature>().Add(signature);
        await Context.SaveChangesAsync();

        var errorLog = new Source.Features.ErrorLog.Models.ErrorLog
        {
            Id = Guid.NewGuid(),
            Message = "boom",
            Source = "HTTP",
            Severity = "Error",
            SignatureId = signature.Id,
        };
        Context.ErrorLogs.Add(errorLog);
        await Context.SaveChangesAsync();

        Context.ChangeTracker.Clear();

        var reloaded = await Context.ErrorLogs
            .Include(e => e.Signature)
            .FirstAsync(e => e.Id == errorLog.Id);

        reloaded.SignatureId.Should().Be(signature.Id);
        reloaded.Signature.Should().NotBeNull();
        reloaded.Signature!.Hash.Should().Be(signature.Hash);
    }

    [Fact]
    public void CompositeIndex_OnLastSeenAtAndIsResolved_ExistsInModel()
    {
        var entityType = Context.Model.FindEntityType(typeof(ErrorSignature));
        entityType.Should().NotBeNull();

        var compositeIndex = entityType!
            .GetIndexes()
            .FirstOrDefault(i =>
                i.Properties.Count == 2
                && i.Properties.Any(p => p.Name == nameof(ErrorSignature.LastSeenAt))
                && i.Properties.Any(p => p.Name == nameof(ErrorSignature.IsResolved)));

        compositeIndex.Should().NotBeNull("there must be a composite index on (LastSeenAt, IsResolved)");
    }

    [Fact]
    public void ErrorLog_SignatureIdIndex_ExistsInModel()
    {
        var entityType = Context.Model.FindEntityType(typeof(Source.Features.ErrorLog.Models.ErrorLog));
        entityType.Should().NotBeNull();

        var fkIndex = entityType!
            .GetIndexes()
            .FirstOrDefault(i => i.Properties.Count == 1 && i.Properties[0].Name == "SignatureId");

        fkIndex.Should().NotBeNull("there must be an index on ErrorLog.SignatureId");
    }

    [Fact]
    public void Migration_FileExists()
    {
        // Verify the migration file was generated with the expected name.
        // Walk up from the test binary to the repo root.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Migrations")))
        {
            dir = dir.Parent;
        }
        dir.Should().NotBeNull("could not locate the Migrations directory from the test binary");

        var migrationsPath = Path.Combine(dir!.FullName, "Migrations");
        var migrations = Directory.GetFiles(migrationsPath, "*_AddErrorSignatures.cs");

        migrations.Should().NotBeEmpty("a migration file ending in '_AddErrorSignatures.cs' must exist");
    }
}

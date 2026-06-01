using Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeImages.Models;

namespace Api.Tests.Features.RuntimeImages;

/// <summary>
/// Smoke tests for the <see cref="RuntimeImage"/> EF model. We stay on the
/// shared in-memory provider here — these are about confirming the DbSet is
/// wired up and round-trips every field, plus that the dominant query
/// patterns (default-spawn selection: latest <c>Active</c> by <c>BuiltAt DESC</c>)
/// actually return what callers will expect. Postgres-specific index ordering
/// is verified by the migration itself and via model metadata, not here.
/// </summary>
public class RuntimeImagePersistenceTests : HandlerTestBase
{
    [Fact]
    public async Task Insert_and_read_back_preserves_all_fields()
    {
        var id = Guid.NewGuid();
        var builtAt = new DateTime(2026, 5, 8, 12, 34, 56, DateTimeKind.Utc);
        var image = new RuntimeImage
        {
            Id = id,
            Tag = "2026.05.08-7af3b21",
            Digest = "sha256:abcdef0123456789",
            Registry = "registry.fly.io/fwd-runtime",
            GitSha = "7af3b21",
            BuiltAt = builtAt,
            SizeMb = 248,
            Status = RuntimeImageStatus.Active,
            Notes = "first published image of the day",
        };
        Context.RuntimeImages.Add(image);
        await Context.SaveChangesAsync();

        // Detach so the next read materialises from the store, not the change tracker.
        Context.ChangeTracker.Clear();

        var loaded = await Context.RuntimeImages.SingleAsync(i => i.Id == id);

        loaded.Tag.Should().Be("2026.05.08-7af3b21");
        loaded.Digest.Should().Be("sha256:abcdef0123456789");
        loaded.Registry.Should().Be("registry.fly.io/fwd-runtime");
        loaded.GitSha.Should().Be("7af3b21");
        loaded.BuiltAt.Should().BeCloseTo(builtAt, TimeSpan.FromSeconds(1));
        loaded.SizeMb.Should().Be(248);
        loaded.Status.Should().Be(RuntimeImageStatus.Active);
        loaded.Notes.Should().Be("first published image of the day");
        loaded.CreatedAt.Should().NotBe(default, "auto-set by SaveChangesAsync via IAuditable");
        loaded.UpdatedAt.Should().NotBe(default, "auto-set by SaveChangesAsync via IAuditable");
    }

    [Fact]
    public void Tag_IsUniqueIndexed()
    {
        // In-memory EF does NOT enforce unique indexes, so verify via model metadata.
        // Postgres enforces this in production — covered by the migration.
        var entityType = Context.Model.FindEntityType(typeof(RuntimeImage));
        entityType.Should().NotBeNull();

        var tagIndex = entityType!
            .GetIndexes()
            .FirstOrDefault(i => i.Properties.Count == 1 && i.Properties[0].Name == nameof(RuntimeImage.Tag));

        tagIndex.Should().NotBeNull("there must be an index on RuntimeImage.Tag");
        tagIndex!.IsUnique.Should().BeTrue("CI must never publish the same tag twice");
    }

    [Fact]
    public void CompositeIndex_OnStatusAndBuiltAt_ExistsInModel()
    {
        var entityType = Context.Model.FindEntityType(typeof(RuntimeImage));
        entityType.Should().NotBeNull();

        var compositeIndex = entityType!
            .GetIndexes()
            .FirstOrDefault(i =>
                i.Properties.Count == 2
                && i.Properties.Any(p => p.Name == nameof(RuntimeImage.Status))
                && i.Properties.Any(p => p.Name == nameof(RuntimeImage.BuiltAt)));

        compositeIndex.Should().NotBeNull("there must be a composite index on (Status, BuiltAt) for default-spawn lookups");
    }

    [Fact]
    public async Task Active_images_come_back_in_descending_built_order()
    {
        // Insert a mix of statuses and BuiltAt values. The dominant read is
        // "latest 10 Active by BuiltAt DESC" — that's how the main API picks
        // the default spawn target.
        var baseTime = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < 12; i++)
        {
            Context.RuntimeImages.Add(new RuntimeImage
            {
                Id = Guid.NewGuid(),
                Tag = $"2026.05.0{i}-active",
                Digest = $"sha256:active{i}",
                Registry = "registry.fly.io/fwd-runtime",
                GitSha = $"active{i}",
                BuiltAt = baseTime.AddHours(i),
                SizeMb = 200 + i,
                Status = RuntimeImageStatus.Active,
            });
        }
        // Noise — non-Active rows that must NOT appear in the query result.
        Context.RuntimeImages.Add(new RuntimeImage
        {
            Id = Guid.NewGuid(),
            Tag = "deprecated-1",
            Digest = "sha256:dep1",
            Registry = "registry.fly.io/fwd-runtime",
            GitSha = "dep1",
            BuiltAt = baseTime.AddHours(100), // newest of all — but deprecated, so excluded
            SizeMb = 250,
            Status = RuntimeImageStatus.Deprecated,
        });
        Context.RuntimeImages.Add(new RuntimeImage
        {
            Id = Guid.NewGuid(),
            Tag = "yanked-1",
            Digest = "sha256:yank1",
            Registry = "registry.fly.io/fwd-runtime",
            GitSha = "yank1",
            BuiltAt = baseTime.AddHours(200), // even newer — but yanked, so excluded
            SizeMb = 250,
            Status = RuntimeImageStatus.Yanked,
        });
        await Context.SaveChangesAsync();
        Context.ChangeTracker.Clear();

        var latest = await Context.RuntimeImages
            .Where(i => i.Status == RuntimeImageStatus.Active)
            .OrderByDescending(i => i.BuiltAt)
            .Take(10)
            .ToListAsync();

        latest.Should().HaveCount(10);
        latest.Should().OnlyContain(i => i.Status == RuntimeImageStatus.Active);
        // Most recently built active image was the i=11 row (BuiltAt = base + 11h).
        latest.First().Tag.Should().Be("2026.05.011-active");
        // Strictly non-increasing BuiltAt — the index promises this ordering.
        latest.Zip(latest.Skip(1))
            .Should()
            .OnlyContain(pair => pair.First.BuiltAt >= pair.Second.BuiltAt);
    }

    [Fact]
    public async Task Status_round_trips_through_string_conversion()
    {
        // Belt-and-braces: the column is mapped as string. If someone removes
        // .HasConversion<string>() we want to know via this round-trip.
        var active = new RuntimeImage
        {
            Id = Guid.NewGuid(),
            Tag = "active-tag",
            Digest = "sha256:a",
            Registry = "registry.fly.io/x",
            GitSha = "a",
            BuiltAt = DateTime.UtcNow,
            SizeMb = 100,
            Status = RuntimeImageStatus.Active,
        };
        var deprecated = new RuntimeImage
        {
            Id = Guid.NewGuid(),
            Tag = "deprecated-tag",
            Digest = "sha256:d",
            Registry = "registry.fly.io/x",
            GitSha = "d",
            BuiltAt = DateTime.UtcNow,
            SizeMb = 100,
            Status = RuntimeImageStatus.Deprecated,
            Notes = "rolled forward",
        };
        var yanked = new RuntimeImage
        {
            Id = Guid.NewGuid(),
            Tag = "yanked-tag",
            Digest = "sha256:y",
            Registry = "registry.fly.io/x",
            GitSha = "y",
            BuiltAt = DateTime.UtcNow,
            SizeMb = 100,
            Status = RuntimeImageStatus.Yanked,
            Notes = "security issue",
        };
        Context.RuntimeImages.AddRange(active, deprecated, yanked);
        await Context.SaveChangesAsync();
        Context.ChangeTracker.Clear();

        var loadedActive = await Context.RuntimeImages.SingleAsync(i => i.Id == active.Id);
        var loadedDeprecated = await Context.RuntimeImages.SingleAsync(i => i.Id == deprecated.Id);
        var loadedYanked = await Context.RuntimeImages.SingleAsync(i => i.Id == yanked.Id);

        loadedActive.Status.Should().Be(RuntimeImageStatus.Active);
        loadedDeprecated.Status.Should().Be(RuntimeImageStatus.Deprecated);
        loadedDeprecated.Notes.Should().Be("rolled forward");
        loadedYanked.Status.Should().Be(RuntimeImageStatus.Yanked);
        loadedYanked.Notes.Should().Be("security issue");
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
        var migrations = Directory.GetFiles(migrationsPath, "*_AddRuntimeImage.cs");

        migrations.Should().NotBeEmpty("a migration file ending in '_AddRuntimeImage.cs' must exist");
    }
}

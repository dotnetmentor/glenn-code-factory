using Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeLifecycle.Models;

namespace Api.Tests.Features.RuntimeLifecycle;

/// <summary>
/// Smoke tests for the <see cref="ProjectRuntime"/> entity. We don't exercise
/// any state-machine behaviour here — that's owned by a follow-up card. We
/// just verify the EF model is wired up correctly:
///
/// <list type="bullet">
///   <item>Round-trip persistence works (auto-set audit fields, defaults).</item>
///   <item><see cref="RuntimeState"/> is configured to persist as a string,
///         not the underlying int — checked via model metadata since the
///         in-memory provider treats both representations identically.</item>
///   <item>The <c>ISoftDelete</c> hook in <c>ApplicationDbContext</c> populates
///         <c>DeletedAt</c> when <c>IsDeleted</c> flips to <c>true</c>, and
///         the global query filter hides the row from default queries.</item>
/// </list>
///
/// Mirrors <c>WorkspaceDbModelTests</c> — same provider, same approach.
/// </summary>
public class ProjectRuntimeEntityTests : HandlerTestBase
{
    [Fact]
    public async Task Can_save_and_retrieve_ProjectRuntime()
    {
        var runtime = new ProjectRuntime
        {
            ProjectId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Region = "arn",
        };

        Context.ProjectRuntimes.Add(runtime);
        await Context.SaveChangesAsync();

        // Force a reload from the in-memory store so we don't read tracked instance
        Context.ChangeTracker.Clear();

        var reloaded = await Context.ProjectRuntimes.SingleAsync(r => r.Id == runtime.Id);

        reloaded.ProjectId.Should().Be(runtime.ProjectId);
        reloaded.TenantId.Should().Be(runtime.TenantId);
        reloaded.Region.Should().Be("arn");
        reloaded.State.Should().Be(RuntimeState.Pending, "default starting state");
        reloaded.VolumeSizeGb.Should().Be(5, "default volume size (5 GB ≈ ~320k inodes, comfortably covers a typical monorepo npm install — see VolumeSizeGb XMLdoc)");
        reloaded.RespawnRetries.Should().Be(0);
        reloaded.IsDeleted.Should().BeFalse();
        reloaded.CreatedAt.Should().NotBe(default, "auto-set by SaveChangesAsync");
        reloaded.UpdatedAt.Should().NotBe(default, "auto-set by SaveChangesAsync");
    }

    [Fact]
    public void State_enum_is_persisted_as_string()
    {
        // The in-memory provider strips the value converter from the model
        // metadata at runtime, so checking via Context.Model is unreliable.
        // What we actually care about is that the relational migration writes
        // a string column for the State property — verify by reading the
        // generated migration file directly. Pattern borrowed from
        // ErrorSignatureEntityTests.Migration_FileExists.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Migrations")))
        {
            dir = dir.Parent;
        }
        dir.Should().NotBeNull("could not locate the Migrations directory from the test binary");

        var migrationsPath = Path.Combine(dir!.FullName, "Migrations");
        var migrationFiles = Directory.GetFiles(migrationsPath, "*_AddProjectRuntime.cs");
        migrationFiles.Should().NotBeEmpty("a migration file ending in '_AddProjectRuntime.cs' must exist");

        var content = File.ReadAllText(migrationFiles.Single());

        // The State column in the CreateTable call must be a string type with
        // maxLength: 32 — that's the on-disk evidence of HasConversion<string>().
        content.Should().Contain(
            "State = table.Column<string>(type: \"character varying(32)\", maxLength: 32, nullable: false)",
            "ProjectRuntime.State must be persisted as a varchar(32) — i.e. HasConversion<string>().HasMaxLength(32)");
    }

    [Fact]
    public async Task Soft_delete_populates_DeletedAt_and_hides_row_from_default_queries()
    {
        var runtime = new ProjectRuntime
        {
            ProjectId = Guid.NewGuid(),
            Region = "arn",
        };
        Context.ProjectRuntimes.Add(runtime);
        await Context.SaveChangesAsync();

        // Flip the soft-delete flag — ApplicationDbContext.SaveChangesAsync
        // is responsible for stamping DeletedAt.
        runtime.IsDeleted = true;
        await Context.SaveChangesAsync();

        // Default query should skip the row thanks to the global filter.
        var hidden = await Context.ProjectRuntimes.FirstOrDefaultAsync(r => r.Id == runtime.Id);
        hidden.Should().BeNull("soft-deleted runtimes are filtered out by the global query filter");

        // Bypassing the filter we should still find it, and DeletedAt must be set.
        var withFilterIgnored = await Context.ProjectRuntimes
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == runtime.Id);
        withFilterIgnored.Should().NotBeNull();
        withFilterIgnored!.DeletedAt.Should().NotBeNull("interceptor in ApplicationDbContext stamps DeletedAt on soft delete");
    }
}

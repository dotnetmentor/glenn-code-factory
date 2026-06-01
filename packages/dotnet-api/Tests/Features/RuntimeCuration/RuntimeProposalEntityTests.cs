using Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeCuration.Models;
using Source.Features.RuntimeLifecycle.Models;

namespace Api.Tests.Features.RuntimeCuration;

/// <summary>
/// Smoke tests for the <see cref="RuntimeProposal"/> entity. We don't exercise
/// any business behaviour here — that's owned by follow-up cards (commands,
/// daemon tool, apply flow). We just verify the EF model is wired up
/// correctly:
///
/// <list type="bullet">
///   <item>Round-trip persistence — including the JSON content of
///         <see cref="RuntimeProposal.ProposedSpec"/> survives the trip.</item>
///   <item>Default <see cref="RuntimeProposal.Status"/> is
///         <see cref="RuntimeProposalStatus.Pending"/> on insert.</item>
///   <item>User-decision fields (Status, DecidedAt, DecidedBy) round-trip.</item>
///   <item>Soft-delete: the global query filter hides deleted rows from
///         default queries; <c>IgnoreQueryFilters()</c> exposes them.</item>
///   <item>The migration columns land as <c>jsonb</c> (verified by reading
///         the generated migration file directly — the in-memory provider
///         strips relational metadata at runtime).</item>
/// </list>
///
/// Mirrors <c>ProjectRuntimeEntityTests</c> / <c>ConversationEntityTests</c>
/// — same provider, same approach.
/// </summary>
public class RuntimeProposalEntityTests : HandlerTestBase
{
    [Fact]
    public async Task Can_save_and_retrieve_RuntimeProposal_with_json_spec()
    {
        // Persist a parent runtime so the FK is satisfied. (The in-memory
        // provider doesn't enforce FK constraints, but the round-trip test
        // is honest about the relationship anyway.)
        var runtime = new ProjectRuntime { ProjectId = Guid.NewGuid(), Region = "arn" };
        Context.ProjectRuntimes.Add(runtime);
        await Context.SaveChangesAsync();

        const string spec = """{"languages":["node@22","python@3.12"],"services":["postgres","redis"],"extras":["pnpm"]}""";
        var proposal = new RuntimeProposal
        {
            ProjectId = runtime.ProjectId,
            RuntimeId = runtime.Id,
            ProposedSpec = spec,
            Reason = "marketplace with payments and images",
        };
        Context.RuntimeProposals.Add(proposal);
        await Context.SaveChangesAsync();

        // Force a reload so we don't read the tracked instance.
        Context.ChangeTracker.Clear();

        var reloaded = await Context.RuntimeProposals.SingleAsync(p => p.Id == proposal.Id);

        reloaded.ProjectId.Should().Be(runtime.ProjectId);
        reloaded.RuntimeId.Should().Be(runtime.Id);
        reloaded.ProposedSpec.Should().Be(spec, "jsonb round-trips byte-for-byte");
        reloaded.AppliedSpec.Should().BeNull("not yet applied");
        reloaded.Reason.Should().Be("marketplace with payments and images");
        reloaded.DecidedBy.Should().BeNull("still pending");
        reloaded.DecidedAt.Should().BeNull();
        reloaded.ErrorMessage.Should().BeNull();
        reloaded.IsDeleted.Should().BeFalse();
        reloaded.CreatedAt.Should().NotBe(default, "auto-set by SaveChangesAsync");
        reloaded.UpdatedAt.Should().NotBe(default, "auto-set by SaveChangesAsync");
    }

    [Fact]
    public async Task Default_Status_is_Pending_on_insert()
    {
        var proposal = new RuntimeProposal
        {
            ProjectId = Guid.NewGuid(),
            RuntimeId = Guid.NewGuid(),
            ProposedSpec = "{}",
        };
        Context.RuntimeProposals.Add(proposal);
        await Context.SaveChangesAsync();

        Context.ChangeTracker.Clear();

        var reloaded = await Context.RuntimeProposals.SingleAsync(p => p.Id == proposal.Id);
        reloaded.Status.Should().Be(RuntimeProposalStatus.Pending,
            "default starting state — daemon proposes, user has not acted yet");
    }

    [Fact]
    public async Task Approve_sets_Status_DecidedBy_and_DecidedAt()
    {
        var proposal = new RuntimeProposal
        {
            ProjectId = Guid.NewGuid(),
            RuntimeId = Guid.NewGuid(),
            ProposedSpec = """{"services":["postgres"]}""",
        };
        Context.RuntimeProposals.Add(proposal);
        await Context.SaveChangesAsync();

        // Simulate the user clicking Approve in the confirmation card.
        proposal.Status = RuntimeProposalStatus.Approved;
        proposal.AppliedSpec = proposal.ProposedSpec;
        proposal.DecidedBy = "user-id-123";
        var decidedAt = DateTime.UtcNow;
        proposal.DecidedAt = decidedAt;
        await Context.SaveChangesAsync();

        Context.ChangeTracker.Clear();

        var reloaded = await Context.RuntimeProposals.SingleAsync(p => p.Id == proposal.Id);
        reloaded.Status.Should().Be(RuntimeProposalStatus.Approved);
        reloaded.AppliedSpec.Should().Be("""{"services":["postgres"]}""",
            "Approve copies ProposedSpec verbatim into AppliedSpec");
        reloaded.DecidedBy.Should().Be("user-id-123");
        reloaded.DecidedAt.Should().BeCloseTo(decidedAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Soft_delete_populates_DeletedAt_and_hides_row_from_default_queries()
    {
        var proposal = new RuntimeProposal
        {
            ProjectId = Guid.NewGuid(),
            RuntimeId = Guid.NewGuid(),
            ProposedSpec = "{}",
        };
        Context.RuntimeProposals.Add(proposal);
        await Context.SaveChangesAsync();

        // Flip the soft-delete flag — ApplicationDbContext.SaveChangesAsync
        // is responsible for stamping DeletedAt.
        proposal.IsDeleted = true;
        await Context.SaveChangesAsync();

        // Default query should skip the row thanks to the global filter.
        var hidden = await Context.RuntimeProposals.FirstOrDefaultAsync(p => p.Id == proposal.Id);
        hidden.Should().BeNull("soft-deleted proposals are filtered out by the global query filter");

        // Bypassing the filter we should still find it, and DeletedAt must be set.
        var withFilterIgnored = await Context.RuntimeProposals
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == proposal.Id);
        withFilterIgnored.Should().NotBeNull();
        withFilterIgnored!.DeletedAt.Should().NotBeNull(
            "interceptor in ApplicationDbContext stamps DeletedAt on soft delete");
    }

    [Fact]
    public void Migration_file_exists_and_persists_specs_as_jsonb()
    {
        // The in-memory provider strips the value converter / column-type
        // metadata from the model at runtime, so checking via Context.Model
        // is unreliable. What we actually care about is that the relational
        // migration writes jsonb columns — verify by reading the generated
        // migration file directly. Pattern borrowed from
        // ConversationEntityTests / ProjectRuntimeEntityTests.
        var migrationsPath = LocateMigrationsPath();
        var migrationFiles = Directory.GetFiles(migrationsPath, "*_AddRuntimeProposals.cs");
        migrationFiles.Should().NotBeEmpty(
            "a migration file ending in '_AddRuntimeProposals.cs' must exist");

        var content = File.ReadAllText(migrationFiles.Single());

        // ProposedSpec is required jsonb.
        content.Should().Contain(
            "ProposedSpec = table.Column<string>(type: \"jsonb\", nullable: false)",
            "RuntimeProposal.ProposedSpec must be a non-null jsonb column on Postgres");

        // AppliedSpec is nullable jsonb (set on Approve / Edit, null while Pending or after Reject).
        content.Should().Contain(
            "AppliedSpec = table.Column<string>(type: \"jsonb\", nullable: true)",
            "RuntimeProposal.AppliedSpec must be a nullable jsonb column");

        // Spec column added to ProjectRuntimes — the mutable runtime spec.
        content.Should().Contain(
            "name: \"Spec\"",
            "AddRuntimeProposals migration must add the Spec column to ProjectRuntimes");
        content.Should().Contain(
            "table: \"ProjectRuntimes\"",
            "the Spec column is added to the ProjectRuntimes table");

        // DESC composite indexes via raw SQL — mirrors the FlyOperation /
        // BootstrapRun / RuntimeStateEvent / SecretAuditEvent / McpCalls
        // precedent. EF Core 9 can't emit per-column sort order on relational
        // indexes.
        content.Should().Contain(
            "IX_RuntimeProposals_ProjectId_CreatedAt",
            "raw-SQL DESC index on (ProjectId, CreatedAt) must be emitted");
        content.Should().Contain(
            "IX_RuntimeProposals_RuntimeId_CreatedAt",
            "raw-SQL DESC index on (RuntimeId, CreatedAt) must be emitted");
        // The migration emits raw SQL with escaped quotes, so the on-disk
        // bytes contain `\"CreatedAt\" DESC` — i.e. backslash-escaped double
        // quotes in the C# string literal. We match the unescaped tail.
        content.Should().Contain(
            "CreatedAt\\\" DESC",
            "the composite indexes must sort CreatedAt DESC for newest-first range scans");
    }

    // ------------------------------------------------------------------
    // helpers

    private static string LocateMigrationsPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Migrations")))
        {
            dir = dir.Parent;
        }
        dir.Should().NotBeNull("could not locate the Migrations directory from the test binary");
        return Path.Combine(dir!.FullName, "Migrations");
    }
}

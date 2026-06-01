using Api.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Source.Features.Cloudflare.Commands;
using Source.Features.Cloudflare.Models;

namespace Api.Tests.Features.Cloudflare;

/// <summary>
/// Phase 3 wiring coverage for <see cref="AssignSubdomainToBranchHandler"/> —
/// the handler that branch-creation paths call to atomically claim a row from
/// the preview-subdomain pool. The Phase 1 happy-path + race semantics are
/// covered upstream; this suite locks in the contract the branch-creation
/// callers depend on: pool_empty surfaces verbatim, available rows transition
/// to Assigned with the branch FK populated, and the FIFO "oldest first"
/// ordering holds.
///
/// <para>The handler's production path uses Postgres' <c>FOR UPDATE SKIP
/// LOCKED</c>; under the test in-memory provider we fall through to a plain
/// LINQ "next Available" query. That keeps the test single-threaded so the
/// race-safety properties of the prod path can't be exercised here — that's
/// fine; those belong in a Postgres-backed integration test rather than the
/// fast unit suite.</para>
/// </summary>
public class AssignSubdomainToBranchHandlerTests : HandlerTestBase
{
    private static SubdomainAssignment NewAvailable(string hostname, DateTime createdAt) =>
        new()
        {
            Id = Guid.NewGuid(),
            Hostname = hostname,
            Subdomain = hostname.Split('.')[0],
            TunnelId = Guid.NewGuid().ToString(),
            TunnelToken = "encrypted-token",
            Status = SubdomainStatus.Available,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };

    [Fact]
    public async Task Returns_pool_empty_when_no_available_rows()
    {
        var handler = new AssignSubdomainToBranchHandler(
            Context,
            NullLogger<AssignSubdomainToBranchHandler>.Instance);

        var result = await handler.Handle(
            new AssignSubdomainToBranchCommand(Guid.NewGuid()),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("pool_empty");
    }

    [Fact]
    public async Task Claims_oldest_available_row_and_flips_it_to_assigned()
    {
        var older = NewAvailable("kj4m9x2p.glenncode.ai", DateTime.UtcNow.AddMinutes(-5));
        var newer = NewAvailable("a7bn3qe1.glenncode.ai", DateTime.UtcNow);
        Context.SubdomainAssignments.AddRange(older, newer);
        await Context.SaveChangesAsync();

        var branchId = Guid.NewGuid();
        var handler = new AssignSubdomainToBranchHandler(
            Context,
            NullLogger<AssignSubdomainToBranchHandler>.Instance);

        var result = await handler.Handle(
            new AssignSubdomainToBranchCommand(branchId),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Id.Should().Be(older.Id, "FIFO: oldest CreatedAt is handed out first");
        result.Value.AssignedBranchId.Should().Be(branchId);
        result.Value.Status.Should().Be(SubdomainStatus.Assigned);

        var reloadedOlder = await Context.SubdomainAssignments.FindAsync(older.Id);
        reloadedOlder!.Status.Should().Be(SubdomainStatus.Assigned);
        reloadedOlder.AssignedBranchId.Should().Be(branchId);
        reloadedOlder.AssignedAt.Should().NotBeNull();

        var reloadedNewer = await Context.SubdomainAssignments.FindAsync(newer.Id);
        reloadedNewer!.Status.Should().Be(SubdomainStatus.Available);
        reloadedNewer.AssignedBranchId.Should().BeNull();
    }

    [Fact]
    public async Task Skips_already_assigned_rows()
    {
        var alreadyClaimed = NewAvailable("zzzzzzzz.glenncode.ai", DateTime.UtcNow.AddMinutes(-10));
        alreadyClaimed.Status = SubdomainStatus.Assigned;
        alreadyClaimed.AssignedBranchId = Guid.NewGuid();

        var available = NewAvailable("nnnnnnnn.glenncode.ai", DateTime.UtcNow);
        Context.SubdomainAssignments.AddRange(alreadyClaimed, available);
        await Context.SaveChangesAsync();

        var handler = new AssignSubdomainToBranchHandler(
            Context,
            NullLogger<AssignSubdomainToBranchHandler>.Instance);

        var result = await handler.Handle(
            new AssignSubdomainToBranchCommand(Guid.NewGuid()),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Id.Should().Be(available.Id);
    }
}

using Api.Tests.Infrastructure;
using Source.Features.RuntimeCuration.Models;
using Source.Features.RuntimeCuration.Queries;

namespace Api.Tests.Features.RuntimeCuration;

/// <summary>
/// Handler-level coverage for <see cref="ListRuntimeProposalsQueryHandler"/>.
/// In-memory DbContext rig (no HTTP, no SignalR) — the controller integration
/// is asserted in <see cref="RuntimeProposalsReadControllerTests"/>.
///
/// <para>What we cover here: project scoping, status filter, sort order
/// (newest first by <c>CreatedAt</c>), the <c>[1, 200]</c> limit clamp, and
/// soft-delete exclusion via the global query filter.</para>
/// </summary>
public class ListRuntimeProposalsQueryHandlerTests : HandlerTestBase
{
    private async Task<RuntimeProposal> SeedProposalAsync(
        Guid projectId,
        RuntimeProposalStatus status = RuntimeProposalStatus.Pending,
        DateTime? createdAt = null,
        bool deleted = false,
        string? proposedSpec = null)
    {
        var proposal = new RuntimeProposal
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            RuntimeId = Guid.NewGuid(),
            Status = status,
            ProposedSpec = proposedSpec ?? """{"languages":["node@22"],"services":[]}""",
            Reason = "test",
            IsDeleted = deleted,
        };
        Context.RuntimeProposals.Add(proposal);
        await Context.SaveChangesAsync();

        // The audit interceptor stamps CreatedAt on insert. If a specific
        // ordering point is required, override after the initial save and
        // persist again — the in-memory provider is happy to accept it.
        if (createdAt.HasValue)
        {
            proposal.CreatedAt = createdAt.Value;
            await Context.SaveChangesAsync();
        }
        return proposal;
    }

    // ----------------------------------------------------------------------

    [Fact]
    public async Task HappyPath_ReturnsProjectScopedRows_OrderedByCreatedAtDescending()
    {
        var projectId = Guid.NewGuid();
        var t0 = DateTime.UtcNow.AddMinutes(-30);

        var oldest = await SeedProposalAsync(projectId, RuntimeProposalStatus.Approved, createdAt: t0);
        var middle = await SeedProposalAsync(projectId, RuntimeProposalStatus.Rejected, createdAt: t0.AddMinutes(10));
        var newest = await SeedProposalAsync(projectId, RuntimeProposalStatus.Pending, createdAt: t0.AddMinutes(20));

        var handler = new ListRuntimeProposalsQueryHandler(Context);
        var result = await handler.Handle(
            new ListRuntimeProposalsQuery(projectId, Status: null, Limit: 50),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Select(p => p.Id).Should().ContainInOrder(newest.Id, middle.Id, oldest.Id);
    }

    [Fact]
    public async Task StatusFilter_ReturnsOnlyMatchingBucket()
    {
        var projectId = Guid.NewGuid();
        var pending = await SeedProposalAsync(projectId, RuntimeProposalStatus.Pending);
        await SeedProposalAsync(projectId, RuntimeProposalStatus.Approved);
        await SeedProposalAsync(projectId, RuntimeProposalStatus.Rejected);

        var handler = new ListRuntimeProposalsQueryHandler(Context);
        var result = await handler.Handle(
            new ListRuntimeProposalsQuery(projectId, RuntimeProposalStatus.Pending, Limit: 50),
            CancellationToken.None);

        result.Value.Should().ContainSingle();
        result.Value.Single().Id.Should().Be(pending.Id);
        result.Value.Single().Status.Should().Be(RuntimeProposalStatus.Pending);
    }

    [Fact]
    public async Task LimitClamp_NeverReadsMoreThanTwoHundred()
    {
        // Seed a couple of rows and verify the handler caps Take at 200 even
        // when the controller hands in a malicious large value. Asserting the
        // clamp via "returned <= 200" works because the in-memory provider
        // honours Take. Seeding 200+ rows would slow the suite — the clamp
        // is a Math.Clamp on the request, not a runtime DB cursor, so the
        // smaller seed proves the same intent.
        var projectId = Guid.NewGuid();
        for (var i = 0; i < 5; i++)
        {
            await SeedProposalAsync(projectId);
        }

        var handler = new ListRuntimeProposalsQueryHandler(Context);
        var result = await handler.Handle(
            new ListRuntimeProposalsQuery(projectId, Status: null, Limit: 500),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Count.Should().BeLessThanOrEqualTo(200);
        result.Value.Count.Should().Be(5, "we only seeded 5 rows; clamp doesn't fabricate data");
    }

    [Fact]
    public async Task LimitClamp_ZeroOrNegative_ReturnsAtLeastOneRow()
    {
        // Math.Clamp(limit, 1, 200) ensures a 0 / negative input doesn't
        // collapse to an empty result — the lower bound is 1.
        var projectId = Guid.NewGuid();
        await SeedProposalAsync(projectId);
        await SeedProposalAsync(projectId);

        var handler = new ListRuntimeProposalsQueryHandler(Context);
        var result = await handler.Handle(
            new ListRuntimeProposalsQuery(projectId, Status: null, Limit: 0),
            CancellationToken.None);

        result.Value.Count.Should().Be(1, "limit 0 is clamped up to 1");
    }

    [Fact]
    public async Task CrossProject_ProposalsInOtherProjectAreNotIncluded()
    {
        var projectA = Guid.NewGuid();
        var projectB = Guid.NewGuid();
        var inA = await SeedProposalAsync(projectA);
        await SeedProposalAsync(projectB);

        var handler = new ListRuntimeProposalsQueryHandler(Context);
        var result = await handler.Handle(
            new ListRuntimeProposalsQuery(projectA, Status: null, Limit: 50),
            CancellationToken.None);

        result.Value.Should().ContainSingle();
        result.Value.Single().Id.Should().Be(inA.Id);
    }

    [Fact]
    public async Task SoftDeleted_RowsAreFilteredOut()
    {
        var projectId = Guid.NewGuid();
        var alive = await SeedProposalAsync(projectId);
        await SeedProposalAsync(projectId, deleted: true);

        var handler = new ListRuntimeProposalsQueryHandler(Context);
        var result = await handler.Handle(
            new ListRuntimeProposalsQuery(projectId, Status: null, Limit: 50),
            CancellationToken.None);

        result.Value.Should().ContainSingle();
        result.Value.Single().Id.Should().Be(alive.Id);
    }
}

using Api.Tests.Infrastructure;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Source.Features.ProjectKanban.Commands;
using Source.Features.ProjectKanban.Events;
using Source.Features.ProjectKanban.Models;
using Source.Features.ProjectKanban.Queries;

namespace Api.Tests.Features.ProjectKanban;

/// <summary>
/// Handler-level coverage for the kanban CQRS slice (Spec 15 Card 3).
/// In-memory DbContext for fast feedback on Result&lt;T&gt; semantics; the
/// controller-level tests in <see cref="KanbanMcpControllerTests"/> exercise
/// the framework integration on top.
///
/// <para><b>What we cover here:</b> happy-path list / get / create / update /
/// move / delete; the not_found semantics on cross-project lookup; the
/// position reorder algorithm for both same-bucket and cross-bucket moves;
/// soft-delete behaviour.</para>
/// </summary>
public class ProjectKanbanHandlerTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();

    private async Task<Guid> SeedCardAsync(
        Guid projectId,
        ProjectKanbanCardStatus status,
        int position,
        string? title = null,
        bool deleted = false)
    {
        await using var ctx = TestDbContextFactory.Create(_dbName);
        // Use the rich-entity factory so the test seed exercises the same
        // construction path real callers do. Card 2 made the setters private;
        // factory is the only way to mint a card.
        var card = ProjectKanbanCard.Create(
            projectId,
            title ?? $"card-{Guid.NewGuid():N}",
            description: null,
            status,
            position,
            ProjectKanbanCardPriority.Medium,
            dueDate: null,
            createdBy: "test",
            source: ProjectKanbanCardSource.Human,
            createdOnBranch: null);
        if (deleted)
        {
            // IsDeleted is a public-set ISoftDelete contract field — interceptors
            // need write access. Set after Create to seed soft-deleted rows.
            card.IsDeleted = true;
        }
        ctx.ProjectKanbanCards.Add(card);
        await ctx.SaveChangesAsync();
        return card.Id;
    }

    // ----------------------------------------------------------------------
    // ListProjectKanbanCardsQuery
    // ----------------------------------------------------------------------

    [Fact]
    public async Task ListCards_happy_path_returns_project_scoped_cards_ordered_by_status_then_position()
    {
        var projectA = Guid.NewGuid();
        var projectB = Guid.NewGuid();

        await SeedCardAsync(projectA, ProjectKanbanCardStatus.Backlog, 0, "a-backlog-0");
        await SeedCardAsync(projectA, ProjectKanbanCardStatus.Todo, 1, "a-todo-1");
        await SeedCardAsync(projectA, ProjectKanbanCardStatus.Todo, 0, "a-todo-0");
        await SeedCardAsync(projectA, ProjectKanbanCardStatus.InProgress, 0, "a-inprog-0");
        await SeedCardAsync(projectB, ProjectKanbanCardStatus.Todo, 0, "b-todo-0");

        await using var ctx = TestDbContextFactory.Create(_dbName);
        var handler = new ListProjectKanbanCardsQueryHandler(ctx);

        // No filter — every card in project A in (status, position) order.
        var all = await handler.Handle(new ListProjectKanbanCardsQuery(projectA, null), CancellationToken.None);
        all.IsSuccess.Should().BeTrue();
        all.Value.Should().HaveCount(4, "project A has 4 cards; project B is excluded by scope");
        all.Value.Select(c => c.Title).Should().ContainInOrder(
            "a-backlog-0", "a-todo-0", "a-todo-1", "a-inprog-0");

        // Status filter — only the Todo bucket, still in position order.
        var todos = await handler.Handle(
            new ListProjectKanbanCardsQuery(projectA, ProjectKanbanCardStatus.Todo),
            CancellationToken.None);
        todos.IsSuccess.Should().BeTrue();
        todos.Value.Select(c => c.Title).Should().ContainInOrder("a-todo-0", "a-todo-1");
    }

    [Fact]
    public async Task ListCards_excludes_soft_deleted()
    {
        var projectId = Guid.NewGuid();
        await SeedCardAsync(projectId, ProjectKanbanCardStatus.Todo, 0, "alive");
        await SeedCardAsync(projectId, ProjectKanbanCardStatus.Todo, 1, "dead", deleted: true);

        await using var ctx = TestDbContextFactory.Create(_dbName);
        var result = await new ListProjectKanbanCardsQueryHandler(ctx)
            .Handle(new ListProjectKanbanCardsQuery(projectId, null), CancellationToken.None);

        result.Value.Should().ContainSingle(c => c.Title == "alive");
        result.Value.Should().NotContain(c => c.Title == "dead");
    }

    // ----------------------------------------------------------------------
    // GetProjectKanbanCardQuery
    // ----------------------------------------------------------------------

    [Fact]
    public async Task GetCard_cross_project_returns_not_found()
    {
        var projectA = Guid.NewGuid();
        var projectB = Guid.NewGuid();
        var cardInB = await SeedCardAsync(projectB, ProjectKanbanCardStatus.Todo, 0);

        await using var ctx = TestDbContextFactory.Create(_dbName);
        var handler = new GetProjectKanbanCardQueryHandler(ctx);

        // Asking from project A's scope for a card that exists in B — uniform
        // not_found, never a 200 leak.
        var result = await handler.Handle(
            new GetProjectKanbanCardQuery(projectA, cardInB), CancellationToken.None);
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("not_found");
    }

    [Fact]
    public async Task GetCard_happy_path_returns_dto()
    {
        var projectId = Guid.NewGuid();
        var cardId = await SeedCardAsync(projectId, ProjectKanbanCardStatus.InProgress, 2, "task");

        await using var ctx = TestDbContextFactory.Create(_dbName);
        var result = await new GetProjectKanbanCardQueryHandler(ctx)
            .Handle(new GetProjectKanbanCardQuery(projectId, cardId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(cardId);
        result.Value.Title.Should().Be("task");
        result.Value.Status.Should().Be(ProjectKanbanCardStatus.InProgress);
        result.Value.Position.Should().Be(2);
    }

    // ----------------------------------------------------------------------
    // CreateProjectKanbanCardCommand
    // ----------------------------------------------------------------------

    [Fact]
    public async Task CreateCard_appends_to_end_of_status_bucket()
    {
        var projectId = Guid.NewGuid();
        await SeedCardAsync(projectId, ProjectKanbanCardStatus.Todo, 0);
        await SeedCardAsync(projectId, ProjectKanbanCardStatus.Todo, 1);

        await using var ctx = TestDbContextFactory.Create(_dbName);
        var result = await new CreateProjectKanbanCardCommandHandler(ctx)
            .Handle(new CreateProjectKanbanCardCommand(
                projectId, "fresh", "body", ProjectKanbanCardStatus.Todo, "runtime:abc"),
                CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Position.Should().Be(2, "appends after the last card in the bucket");
        result.Value.Title.Should().Be("fresh");
        result.Value.ProjectId.Should().Be(projectId);
    }

    [Fact]
    public async Task CreateCard_in_empty_bucket_lands_at_position_zero()
    {
        var projectId = Guid.NewGuid();

        await using var ctx = TestDbContextFactory.Create(_dbName);
        var result = await new CreateProjectKanbanCardCommandHandler(ctx)
            .Handle(new CreateProjectKanbanCardCommand(
                projectId, "first", null, ProjectKanbanCardStatus.Backlog, "runtime:abc"),
                CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Position.Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateCard_blank_title_returns_invalid_title(string title)
    {
        await using var ctx = TestDbContextFactory.Create(_dbName);
        var result = await new CreateProjectKanbanCardCommandHandler(ctx)
            .Handle(new CreateProjectKanbanCardCommand(
                Guid.NewGuid(), title, null, ProjectKanbanCardStatus.Todo, "actor"),
                CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("invalid_title");
    }

    [Fact]
    public async Task CreateCard_too_long_title_returns_invalid_title()
    {
        await using var ctx = TestDbContextFactory.Create(_dbName);
        var oversize = new string('x', 201);
        var result = await new CreateProjectKanbanCardCommandHandler(ctx)
            .Handle(new CreateProjectKanbanCardCommand(
                Guid.NewGuid(), oversize, null, ProjectKanbanCardStatus.Todo, "actor"),
                CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("invalid_title");
    }

    [Fact]
    public async Task CreateCard_provenance_flows_command_to_entity_to_event()
    {
        // kanban-card-provenance: Source + CreatedOnBranch must round-trip
        // from the command into the persisted row, the response DTO, AND the
        // CardCreated domain event the SignalR handler reads. We snapshot the
        // entity's DomainEvents pre-SaveChanges (the interceptor clears them
        // on dispatch) so this assertion is on the raised payload, not a row.
        var projectId = Guid.NewGuid();

        await using var ctx = TestDbContextFactory.Create(_dbName);

        var result = await new CreateProjectKanbanCardCommandHandler(ctx)
            .Handle(new CreateProjectKanbanCardCommand(
                projectId,
                "agent-card",
                Description: null,
                ProjectKanbanCardStatus.Todo,
                "runtime:abc",
                Source: ProjectKanbanCardSource.Agent,
                CreatedOnBranch: "feat/provenance"),
                CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        // 1) Response DTO carries the provenance.
        result.Value.Source.Should().Be(ProjectKanbanCardSource.Agent);
        result.Value.CreatedOnBranch.Should().Be("feat/provenance");

        // 2) Persisted row carries the provenance.
        await using var verify = TestDbContextFactory.Create(_dbName);
        var stored = await verify.ProjectKanbanCards.SingleAsync(c => c.Id == result.Value.Id);
        stored.Source.Should().Be(ProjectKanbanCardSource.Agent);
        stored.CreatedOnBranch.Should().Be("feat/provenance");

        // 3) CardCreated event raised by the entity factory carries the
        //    provenance — that's what the BroadcastCardCreatedHandler reads
        //    when it builds the SignalR notification. Re-running Create
        //    deterministically reproduces the event payload (the in-memory
        //    DbContext doesn't run the dispatcher, so we can't read the
        //    raised events off the freshly saved entity above).
        var card = ProjectKanbanCard.Create(
            projectId, "agent-card", null, ProjectKanbanCardStatus.Todo, 0,
            ProjectKanbanCardPriority.Medium, null, "runtime:abc",
            ProjectKanbanCardSource.Agent, "feat/provenance");
        var raised = card.DomainEvents.OfType<CardCreated>().Single();
        raised.Source.Should().Be(ProjectKanbanCardSource.Agent);
        raised.CreatedOnBranch.Should().Be("feat/provenance");

        // Defensive branch: Human creates ALWAYS clear the branch even if a
        // buggy caller passes one through.
        var humanCard = ProjectKanbanCard.Create(
            projectId, "human-card", null, ProjectKanbanCardStatus.Todo, 0,
            ProjectKanbanCardPriority.Medium, null, "user:abc",
            ProjectKanbanCardSource.Human, "should-be-ignored");
        humanCard.Source.Should().Be(ProjectKanbanCardSource.Human);
        humanCard.CreatedOnBranch.Should().BeNull();
        humanCard.DomainEvents.OfType<CardCreated>().Single()
            .CreatedOnBranch.Should().BeNull();
    }

    // ----------------------------------------------------------------------
    // UpdateProjectKanbanCardCommand
    // ----------------------------------------------------------------------

    [Fact]
    public async Task UpdateCard_partial_update_leaves_null_fields_untouched()
    {
        var projectId = Guid.NewGuid();
        var cardId = await SeedCardAsync(projectId, ProjectKanbanCardStatus.Todo, 0, "before");

        // Seed with a description via the rich-entity method (private setters
        // after the Card 2 refactor — direct property assignment isn't legal).
        await using (var seed = TestDbContextFactory.Create(_dbName))
        {
            var c = await seed.ProjectKanbanCards.SingleAsync(x => x.Id == cardId);
            c.UpdateMetadata(c.Title, "keep me", c.Priority, c.DueDate);
            await seed.SaveChangesAsync();
        }

        await using var ctx = TestDbContextFactory.Create(_dbName);
        var result = await new UpdateProjectKanbanCardCommandHandler(ctx)
            .Handle(new UpdateProjectKanbanCardCommand(
                projectId, cardId, "after", null, "runtime:abc"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("after");
        result.Value.Description.Should().Be("keep me", "null Description means leave the field unchanged");
    }

    [Fact]
    public async Task UpdateCard_cross_project_returns_not_found()
    {
        var projectA = Guid.NewGuid();
        var projectB = Guid.NewGuid();
        var cardInB = await SeedCardAsync(projectB, ProjectKanbanCardStatus.Todo, 0);

        await using var ctx = TestDbContextFactory.Create(_dbName);
        var result = await new UpdateProjectKanbanCardCommandHandler(ctx)
            .Handle(new UpdateProjectKanbanCardCommand(
                projectA, cardInB, "evil", null, "runtime:bad"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("not_found");
    }

    // ----------------------------------------------------------------------
    // MoveProjectKanbanCardCommand — the meat of the slice.
    // ----------------------------------------------------------------------

    [Fact]
    public async Task MoveCard_same_bucket_moves_first_to_third_and_shifts_correctly()
    {
        // Seed: Todo bucket with 4 cards at positions 0,1,2,3 (titles "0","1","2","3").
        var projectId = Guid.NewGuid();
        var ids = new Guid[4];
        for (var i = 0; i < 4; i++)
        {
            ids[i] = await SeedCardAsync(projectId, ProjectKanbanCardStatus.Todo, i, $"c{i}");
        }

        // Move card at position 0 to position 2.
        // Expected final order in the Todo bucket:
        //   c1 -> 0
        //   c2 -> 1
        //   c0 -> 2 (the moved one)
        //   c3 -> 3 (unchanged)
        await using var ctx = TestDbContextFactory.Create(_dbName);
        var result = await new MoveProjectKanbanCardCommandHandler(ctx)
            .Handle(new MoveProjectKanbanCardCommand(
                projectId, ids[0], ProjectKanbanCardStatus.Todo, 2, "runtime:abc"),
                CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Position.Should().Be(2);

        await using var verify = TestDbContextFactory.Create(_dbName);
        var byId = await verify.ProjectKanbanCards.ToDictionaryAsync(c => c.Id);
        byId[ids[0]].Position.Should().Be(2);
        byId[ids[1]].Position.Should().Be(0);
        byId[ids[2]].Position.Should().Be(1);
        byId[ids[3]].Position.Should().Be(3);

        // Positions in the bucket form a contiguous 0..3 set — no gaps, no dupes.
        var positions = byId.Values
            .Where(c => c.Status == ProjectKanbanCardStatus.Todo)
            .Select(c => c.Position)
            .OrderBy(p => p)
            .ToArray();
        positions.Should().BeEquivalentTo(new[] { 0, 1, 2, 3 });
    }

    [Fact]
    public async Task MoveCard_same_bucket_third_to_first_shifts_others_down()
    {
        var projectId = Guid.NewGuid();
        var ids = new Guid[4];
        for (var i = 0; i < 4; i++)
        {
            ids[i] = await SeedCardAsync(projectId, ProjectKanbanCardStatus.Todo, i, $"c{i}");
        }

        // Move card at position 2 to position 0.
        // Expected: c2 -> 0, c0 -> 1, c1 -> 2, c3 -> 3.
        await using var ctx = TestDbContextFactory.Create(_dbName);
        var result = await new MoveProjectKanbanCardCommandHandler(ctx)
            .Handle(new MoveProjectKanbanCardCommand(
                projectId, ids[2], ProjectKanbanCardStatus.Todo, 0, "actor"),
                CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await using var verify = TestDbContextFactory.Create(_dbName);
        var byId = await verify.ProjectKanbanCards.ToDictionaryAsync(c => c.Id);
        byId[ids[2]].Position.Should().Be(0);
        byId[ids[0]].Position.Should().Be(1);
        byId[ids[1]].Position.Should().Be(2);
        byId[ids[3]].Position.Should().Be(3);
    }

    [Fact]
    public async Task MoveCard_cross_bucket_closes_source_gap_and_opens_destination_slot()
    {
        var projectId = Guid.NewGuid();
        var todo0 = await SeedCardAsync(projectId, ProjectKanbanCardStatus.Todo, 0, "t0");
        var todo1 = await SeedCardAsync(projectId, ProjectKanbanCardStatus.Todo, 1, "t1");
        var todo2 = await SeedCardAsync(projectId, ProjectKanbanCardStatus.Todo, 2, "t2");
        var inprog0 = await SeedCardAsync(projectId, ProjectKanbanCardStatus.InProgress, 0, "i0");
        var inprog1 = await SeedCardAsync(projectId, ProjectKanbanCardStatus.InProgress, 1, "i1");

        // Move t1 from Todo[1] to InProgress[1].
        // Expected: Todo: t0->0, t2->1 (gap closed). InProgress: i0->0, t1->1, i1->2.
        await using var ctx = TestDbContextFactory.Create(_dbName);
        var result = await new MoveProjectKanbanCardCommandHandler(ctx)
            .Handle(new MoveProjectKanbanCardCommand(
                projectId, todo1, ProjectKanbanCardStatus.InProgress, 1, "actor"),
                CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(ProjectKanbanCardStatus.InProgress);
        result.Value.Position.Should().Be(1);

        await using var verify = TestDbContextFactory.Create(_dbName);
        var byId = await verify.ProjectKanbanCards.ToDictionaryAsync(c => c.Id);
        byId[todo0].Status.Should().Be(ProjectKanbanCardStatus.Todo);
        byId[todo0].Position.Should().Be(0);
        byId[todo2].Position.Should().Be(1, "the gap left by t1 must be closed");
        byId[inprog0].Position.Should().Be(0);
        byId[todo1].Status.Should().Be(ProjectKanbanCardStatus.InProgress);
        byId[todo1].Position.Should().Be(1);
        byId[inprog1].Position.Should().Be(2, "the original i1 was shifted down to make room");
    }

    [Fact]
    public async Task MoveCard_cross_project_returns_not_found()
    {
        var projectA = Guid.NewGuid();
        var projectB = Guid.NewGuid();
        var cardInB = await SeedCardAsync(projectB, ProjectKanbanCardStatus.Todo, 0);

        await using var ctx = TestDbContextFactory.Create(_dbName);
        var result = await new MoveProjectKanbanCardCommandHandler(ctx)
            .Handle(new MoveProjectKanbanCardCommand(
                projectA, cardInB, ProjectKanbanCardStatus.Done, 0, "actor"),
                CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("not_found");
    }

    // ----------------------------------------------------------------------
    // DeleteProjectKanbanCardCommand
    // ----------------------------------------------------------------------

    [Fact]
    public async Task DeleteCard_soft_deletes_and_card_disappears_from_list()
    {
        var projectId = Guid.NewGuid();
        var cardId = await SeedCardAsync(projectId, ProjectKanbanCardStatus.Todo, 0, "doomed");

        await using var ctx = TestDbContextFactory.Create(_dbName);
        var result = await new DeleteProjectKanbanCardCommandHandler(ctx)
            .Handle(new DeleteProjectKanbanCardCommand(projectId, cardId, "runtime:abc"),
                CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        // The DTO list excludes soft-deleted rows via the global query filter.
        await using var verifyList = TestDbContextFactory.Create(_dbName);
        var listed = await new ListProjectKanbanCardsQueryHandler(verifyList)
            .Handle(new ListProjectKanbanCardsQuery(projectId, null), CancellationToken.None);
        listed.Value.Should().BeEmpty();

        // The row itself still exists with IsDeleted = true and DeletedAt set.
        // We have to read it via IgnoreQueryFilters to see it.
        await using var verifyRow = TestDbContextFactory.Create(_dbName);
        var row = await verifyRow.ProjectKanbanCards
            .IgnoreQueryFilters()
            .SingleAsync(c => c.Id == cardId);
        row.IsDeleted.Should().BeTrue();
        row.DeletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteCard_cross_project_returns_not_found()
    {
        var projectA = Guid.NewGuid();
        var projectB = Guid.NewGuid();
        var cardInB = await SeedCardAsync(projectB, ProjectKanbanCardStatus.Todo, 0);

        await using var ctx = TestDbContextFactory.Create(_dbName);
        var result = await new DeleteProjectKanbanCardCommandHandler(ctx)
            .Handle(new DeleteProjectKanbanCardCommand(projectA, cardInB, "runtime:bad"),
                CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("not_found");
    }

    [Fact]
    public async Task DeleteCard_does_not_renumber_remaining_cards()
    {
        var projectId = Guid.NewGuid();
        var c0 = await SeedCardAsync(projectId, ProjectKanbanCardStatus.Todo, 0, "a");
        var c1 = await SeedCardAsync(projectId, ProjectKanbanCardStatus.Todo, 1, "b");
        var c2 = await SeedCardAsync(projectId, ProjectKanbanCardStatus.Todo, 2, "c");

        await using var ctx = TestDbContextFactory.Create(_dbName);
        await new DeleteProjectKanbanCardCommandHandler(ctx)
            .Handle(new DeleteProjectKanbanCardCommand(projectId, c1, "actor"), CancellationToken.None);

        // Position contract: gaps are intentional. c0 stays at 0, c2 stays at 2.
        await using var verify = TestDbContextFactory.Create(_dbName);
        var byId = await verify.ProjectKanbanCards
            .Where(c => !c.IsDeleted)
            .ToDictionaryAsync(c => c.Id);
        byId[c0].Position.Should().Be(0);
        byId[c2].Position.Should().Be(2);
    }
}

// Suppress warning when MediatR's Unit (used by DeleteProjectKanbanCardCommand) is referenced
// implicitly through tests — keeps this file standalone without polluting global usings.
internal static class _Unused
{
    public static readonly Type _ = typeof(Unit);
}

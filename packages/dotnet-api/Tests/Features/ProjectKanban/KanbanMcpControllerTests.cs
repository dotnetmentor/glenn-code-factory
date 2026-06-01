using System.Security.Claims;
using Api.Tests.Infrastructure;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Source.Features.Mcp.Framework;
using Source.Features.Mcp.Models;
using Source.Features.ProjectKanban.Commands;
using Source.Features.ProjectKanban.Mcp;
using Source.Features.ProjectKanban.Models;
using Source.Features.ProjectKanban.Queries;
using Source.Features.RuntimeTokens.Models;
using Source.Infrastructure;
using Source.Shared;

namespace Api.Tests.Features.ProjectKanban;

/// <summary>
/// Controller-level coverage for the kanban MCP — verifies the framework
/// integration (forbidden-field strip, audit row write, envelope shape,
/// claims-derived <c>ProjectId</c>) on top of the CQRS handlers.
///
/// <para>Mirrors the
/// <see cref="Api.Tests.Features.Mcp.McpControllerBaseTests"/> shape: stamp a
/// <see cref="DefaultHttpContext"/> pre-populated with the RuntimeToken
/// claims, instantiate the controller, call the action directly. We use a
/// real in-memory <see cref="ApplicationDbContext"/> from
/// <see cref="TestDbContextFactory"/> and a real <see cref="MediatR"/>
/// pipeline — the handlers are cheap so wiring them properly catches any
/// regression in the controller-handler seam.</para>
/// </summary>
public class KanbanMcpControllerTests
{
    /// <summary>
    /// Build a fully-wired controller bound to <paramref name="db"/>. The
    /// <see cref="IMediator"/> is wired by hand (not the full DI container)
    /// so the test stays self-contained — we register the handler types we
    /// use and dispatch directly.
    /// </summary>
    private static KanbanMcpController CreateController(
        ApplicationDbContext db,
        Guid runtimeId,
        Guid projectId)
    {
        var mediator = BuildMediator(db);

        var identity = new ClaimsIdentity(authenticationType: "RuntimeToken");
        identity.AddClaim(new Claim(RuntimeTokenClaimNames.RuntimeId, runtimeId.ToString()));
        identity.AddClaim(new Claim(RuntimeTokenClaimNames.ProjectId, projectId.ToString()));
        var principal = new ClaimsPrincipal(identity);

        var httpContext = new DefaultHttpContext { User = principal };

        return new KanbanMcpController(
            mediator,
            db,
            NullLogger<McpControllerBase>.Instance,
            new McpRateLimiter(new SystemClock(), NullLogger<McpRateLimiter>.Instance))
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    /// <summary>
    /// Hand-wired MediatR pipeline. ServiceCollection-based DI would also
    /// work but pulls in the whole MediatR registration machinery; this is
    /// the smaller surface.
    /// </summary>
    private static IMediator BuildMediator(ApplicationDbContext db)
    {
        var mediator = new Mock<IMediator>();

        mediator
            .Setup(m => m.Send(It.IsAny<ListProjectKanbanCardsQuery>(), It.IsAny<CancellationToken>()))
            .Returns<ListProjectKanbanCardsQuery, CancellationToken>(
                (q, ct) => new ListProjectKanbanCardsQueryHandler(db).Handle(q, ct));
        mediator
            .Setup(m => m.Send(It.IsAny<GetProjectKanbanCardQuery>(), It.IsAny<CancellationToken>()))
            .Returns<GetProjectKanbanCardQuery, CancellationToken>(
                (q, ct) => new GetProjectKanbanCardQueryHandler(db).Handle(q, ct));
        mediator
            .Setup(m => m.Send(It.IsAny<CreateProjectKanbanCardCommand>(), It.IsAny<CancellationToken>()))
            .Returns<CreateProjectKanbanCardCommand, CancellationToken>(
                (c, ct) => new CreateProjectKanbanCardCommandHandler(db).Handle(c, ct));
        mediator
            .Setup(m => m.Send(It.IsAny<UpdateProjectKanbanCardCommand>(), It.IsAny<CancellationToken>()))
            .Returns<UpdateProjectKanbanCardCommand, CancellationToken>(
                (c, ct) => new UpdateProjectKanbanCardCommandHandler(db).Handle(c, ct));
        mediator
            .Setup(m => m.Send(It.IsAny<MoveProjectKanbanCardCommand>(), It.IsAny<CancellationToken>()))
            .Returns<MoveProjectKanbanCardCommand, CancellationToken>(
                (c, ct) => new MoveProjectKanbanCardCommandHandler(db).Handle(c, ct));
        mediator
            .Setup(m => m.Send(It.IsAny<DeleteProjectKanbanCardCommand>(), It.IsAny<CancellationToken>()))
            .Returns<DeleteProjectKanbanCardCommand, CancellationToken>(
                (c, ct) => new DeleteProjectKanbanCardCommandHandler(db).Handle(c, ct));

        return mediator.Object;
    }

    private static McpResponse<T> AsEnvelope<T>(IActionResult result)
    {
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        return ok.Value.Should().BeOfType<McpResponse<T>>().Subject;
    }

    // ----------------------------------------------------------------------
    // listCards
    // ----------------------------------------------------------------------

    [Fact]
    public async Task listCards_happy_path_returns_status_filtered_cards()
    {
        await using var db = TestDbContextFactory.Create();
        var projectId = Guid.NewGuid();

        // Seed three cards across two statuses via the rich-entity factory
        // (private setters after the Card 2 refactor).
        db.ProjectKanbanCards.AddRange(
            ProjectKanbanCard.Create(projectId, "todo-0", null, ProjectKanbanCardStatus.Todo, 0, ProjectKanbanCardPriority.Medium, null, null, ProjectKanbanCardSource.Human, null),
            ProjectKanbanCard.Create(projectId, "todo-1", null, ProjectKanbanCardStatus.Todo, 1, ProjectKanbanCardPriority.Medium, null, null, ProjectKanbanCardSource.Human, null),
            ProjectKanbanCard.Create(projectId, "done-0", null, ProjectKanbanCardStatus.Done, 0, ProjectKanbanCardPriority.Medium, null, null, ProjectKanbanCardSource.Human, null));
        await db.SaveChangesAsync();

        var controller = CreateController(db, Guid.NewGuid(), projectId);

        // No filter — three cards.
        var allResult = await controller.ListCards(new ListCardsInput(), CancellationToken.None);
        var allEnvelope = AsEnvelope<List<ProjectKanbanCardListItemDto>>(allResult);
        allEnvelope.Error.Should().BeNull();
        allEnvelope.Result.Should().HaveCount(3);

        // Status filter — only the Todo bucket.
        var todoResult = await controller.ListCards(
            new ListCardsInput { Status = ProjectKanbanCardStatus.Todo }, CancellationToken.None);
        var todoEnvelope = AsEnvelope<List<ProjectKanbanCardListItemDto>>(todoResult);
        todoEnvelope.Result.Should().HaveCount(2);
        todoEnvelope.Result!.Select(c => c.Title).Should().ContainInOrder("todo-0", "todo-1");
    }

    // ----------------------------------------------------------------------
    // getCard — cross-project returns 200 + not_found error envelope, audit row
    // logged with Status = ClientError, no leak of the other tenant's card.
    // ----------------------------------------------------------------------

    [Fact]
    public async Task getCard_cross_project_returns_not_found_and_audits_ClientError()
    {
        await using var db = TestDbContextFactory.Create();
        var projectA = Guid.NewGuid();
        var projectB = Guid.NewGuid();

        var cardInB = ProjectKanbanCard.Create(
            projectB, "secret", null, ProjectKanbanCardStatus.Todo, 0,
            ProjectKanbanCardPriority.Medium, null, null,
            ProjectKanbanCardSource.Human, null);
        db.ProjectKanbanCards.Add(cardInB);
        await db.SaveChangesAsync();

        // Caller's claims = projectA, asking for a card that lives in B.
        var controller = CreateController(db, Guid.NewGuid(), projectA);

        var result = await controller.GetCard(new GetCardInput { CardId = cardInB.Id }, CancellationToken.None);

        // HTTP 200 (per framework convention) but envelope.Error populated with
        // not_found — never a 403, never a leak of B's card.
        var envelope = AsEnvelope<ProjectKanbanCardDto>(result);
        envelope.Result.Should().BeNull();
        envelope.Error.Should().NotBeNull();
        envelope.Error!.Code.Should().Be("not_found");

        // Audit row written with Status = ClientError (the framework buckets
        // every Result.Failure that way per its current contract).
        var audit = await db.McpCalls.SingleAsync();
        audit.Status.Should().Be(McpCallStatus.ClientError);
        audit.ErrorCode.Should().Be("not_found");
        audit.ServerName.Should().Be("kanban");
        audit.Method.Should().Be("getCard");
    }

    // ----------------------------------------------------------------------
    // createCard — body smuggling a foreign projectId must be ignored; the
    // card lands in the claims project. Verifies the framework's strip works
    // end-to-end on a real consumer.
    // ----------------------------------------------------------------------

    /// <summary>
    /// Variant of <see cref="CreateCardInput"/> with a forbidden
    /// <c>ProjectId</c> field — the framework's strip should zero it before
    /// the handler sees it. Defined as a writable record so reflection can
    /// clear the field.
    /// </summary>
    private sealed record MaliciousCreateCardInput
    {
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        public ProjectKanbanCardStatus Status { get; set; }
        public Guid? ProjectId { get; set; } // forbidden — must be stripped
    }

    [Fact]
    public async Task createCard_with_malicious_projectId_in_body_lands_in_claims_project()
    {
        await using var db = TestDbContextFactory.Create();
        var claimsProject = Guid.NewGuid();
        var attackerProject = Guid.NewGuid();

        // We can't go through the typed CreateCard action because its DTO
        // doesn't carry ProjectId — but the framework's strip is applied to
        // the input record passed into InvokeAsync regardless of shape. We
        // simulate the wire-level abuse by using a custom input type that
        // matches the daemon's MCP body shape plus the forbidden field, then
        // dispatching via a thin pass-through controller subclass.
        var captured = new List<MaliciousCreateCardInput>();
        Task<Source.Shared.Results.Result<ProjectKanbanCardDto>> Handler(MaliciousCreateCardInput? i)
        {
            captured.Add(i!);
            return new CreateProjectKanbanCardCommandHandler(db).Handle(
                new CreateProjectKanbanCardCommand(
                    // Simulating what KanbanMcpController does: passes
                    // ProjectId from claims, NEVER from input.
                    claimsProject,
                    i!.Title,
                    i.Description,
                    i.Status,
                    "runtime:test"),
                CancellationToken.None);
        }

        var probe = new ProbeMcpController(
            db,
            NullLogger<McpControllerBase>.Instance,
            new McpRateLimiter(new SystemClock(), NullLogger<McpRateLimiter>.Instance),
            Handler);
        var identity = new ClaimsIdentity(authenticationType: "RuntimeToken");
        identity.AddClaim(new Claim(RuntimeTokenClaimNames.RuntimeId, Guid.NewGuid().ToString()));
        identity.AddClaim(new Claim(RuntimeTokenClaimNames.ProjectId, claimsProject.ToString()));
        probe.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };

        var malicious = new MaliciousCreateCardInput
        {
            Title = "real",
            Status = ProjectKanbanCardStatus.Todo,
            ProjectId = attackerProject, // smuggled
        };

        var result = await probe.Run(malicious, CancellationToken.None);
        var envelope = AsEnvelope<ProjectKanbanCardDto>(result);
        envelope.Error.Should().BeNull("the forbidden-field strip must not break the call");
        envelope.Result!.ProjectId.Should().Be(claimsProject);

        captured.Single().ProjectId.Should().BeNull("the strip must zero ProjectId before the handler runs");

        // The card landed in the claims project, NOT the attacker's project.
        var stored = await db.ProjectKanbanCards.SingleAsync();
        stored.ProjectId.Should().Be(claimsProject);
    }

    /// <summary>
    /// Minimal probe controller inheriting <see cref="McpControllerBase"/>
    /// so the framework's <c>InvokeAsync</c> wiring (claim resolve, strip,
    /// audit) runs around an arbitrary input/output shape. Used by the
    /// "malicious projectId" scenario where we need to inject a custom
    /// input type into the same pipeline.
    /// </summary>
    [McpServer(name: "kanban", version: "v1")]
    private sealed class ProbeMcpController : McpControllerBase
    {
        private readonly Func<MaliciousCreateCardInput?, Task<Source.Shared.Results.Result<ProjectKanbanCardDto>>> _h;

        public ProbeMcpController(
            ApplicationDbContext db,
            ILogger<McpControllerBase> logger,
            McpRateLimiter rateLimiter,
            Func<MaliciousCreateCardInput?, Task<Source.Shared.Results.Result<ProjectKanbanCardDto>>> h)
            : base(db, logger, rateLimiter)
        {
            _h = h;
        }

        public Task<IActionResult> Run(MaliciousCreateCardInput? input, CancellationToken ct) =>
            InvokeAsync("createCard", input, _h, ct);
    }

    // ----------------------------------------------------------------------
    // moveCard — the atomic-reorder smoke at the controller level.
    // ----------------------------------------------------------------------

    [Fact]
    public async Task moveCard_reorders_positions_atomically_through_controller()
    {
        await using var db = TestDbContextFactory.Create();
        var projectId = Guid.NewGuid();
        var ids = new Guid[4];
        for (var i = 0; i < 4; i++)
        {
            var c = ProjectKanbanCard.Create(
                projectId, $"c{i}", null, ProjectKanbanCardStatus.Todo, i,
                ProjectKanbanCardPriority.Medium, null, null,
                ProjectKanbanCardSource.Human, null);
            db.ProjectKanbanCards.Add(c);
            ids[i] = c.Id;
        }
        await db.SaveChangesAsync();

        var controller = CreateController(db, Guid.NewGuid(), projectId);

        // Move c0 (position 0) to position 2.
        var result = await controller.MoveCard(
            new MoveCardInput
            {
                CardId = ids[0],
                NewStatus = ProjectKanbanCardStatus.Todo,
                NewPosition = 2,
            },
            CancellationToken.None);

        var envelope = AsEnvelope<ProjectKanbanCardDto>(result);
        envelope.Error.Should().BeNull();
        envelope.Result!.Position.Should().Be(2);

        var byId = await db.ProjectKanbanCards.ToDictionaryAsync(c => c.Id);
        byId[ids[0]].Position.Should().Be(2);
        byId[ids[1]].Position.Should().Be(0);
        byId[ids[2]].Position.Should().Be(1);
        byId[ids[3]].Position.Should().Be(3);
    }

    // ----------------------------------------------------------------------
    // deleteCard — soft-delete via the framework, list no longer surfaces it.
    // ----------------------------------------------------------------------

    [Fact]
    public async Task deleteCard_soft_deletes_and_list_excludes_it()
    {
        await using var db = TestDbContextFactory.Create();
        var projectId = Guid.NewGuid();
        var card = ProjectKanbanCard.Create(
            projectId, "doomed", null, ProjectKanbanCardStatus.Todo, 0,
            ProjectKanbanCardPriority.Medium, null, null,
            ProjectKanbanCardSource.Human, null);
        db.ProjectKanbanCards.Add(card);
        await db.SaveChangesAsync();

        var controller = CreateController(db, Guid.NewGuid(), projectId);

        var deleteResult = await controller.DeleteCard(
            new DeleteCardInput { CardId = card.Id }, CancellationToken.None);
        var deleteEnvelope = AsEnvelope<Unit>(deleteResult);
        deleteEnvelope.Error.Should().BeNull();

        // List no longer returns the soft-deleted card.
        var listResult = await controller.ListCards(new ListCardsInput(), CancellationToken.None);
        var listEnvelope = AsEnvelope<List<ProjectKanbanCardListItemDto>>(listResult);
        listEnvelope.Result.Should().BeEmpty();

        // The row itself is still there with IsDeleted = true.
        var row = await db.ProjectKanbanCards.IgnoreQueryFilters().SingleAsync();
        row.IsDeleted.Should().BeTrue();
    }

    // ----------------------------------------------------------------------
    // Audit smoke: every method writes one McpCall row.
    // ----------------------------------------------------------------------

    [Fact]
    public async Task all_six_methods_each_write_one_McpCall_audit_row()
    {
        await using var db = TestDbContextFactory.Create();
        var projectId = Guid.NewGuid();
        var controller = CreateController(db, Guid.NewGuid(), projectId);

        // 1. createCard — generates a real card we can target with the others.
        var created = AsEnvelope<ProjectKanbanCardDto>(
            await controller.CreateCard(new CreateCardInput
            {
                Title = "smoke",
                Status = ProjectKanbanCardStatus.Todo,
            }, CancellationToken.None));
        created.Error.Should().BeNull();
        var cardId = created.Result!.Id;

        // 2. listCards.
        await controller.ListCards(new ListCardsInput(), CancellationToken.None);

        // 3. getCard.
        await controller.GetCard(new GetCardInput { CardId = cardId }, CancellationToken.None);

        // 4. updateCard.
        await controller.UpdateCard(new UpdateCardInput
        {
            CardId = cardId,
            Title = "renamed",
        }, CancellationToken.None);

        // 5. moveCard (within the same bucket — no-op-ish but still produces an audit row).
        await controller.MoveCard(new MoveCardInput
        {
            CardId = cardId,
            NewStatus = ProjectKanbanCardStatus.Todo,
            NewPosition = 0,
        }, CancellationToken.None);

        // 6. deleteCard.
        await controller.DeleteCard(new DeleteCardInput { CardId = cardId }, CancellationToken.None);

        var rows = await db.McpCalls.ToListAsync();
        rows.Should().HaveCount(6, "exactly one McpCall row per MCP method invocation");
        rows.Select(r => r.Method).Should().BeEquivalentTo(
            "createCard", "listCards", "getCard", "updateCard", "moveCard", "deleteCard");
        rows.Should().AllSatisfy(r =>
        {
            r.ServerName.Should().Be("kanban");
            r.Status.Should().Be(McpCallStatus.Success);
        });
    }
}

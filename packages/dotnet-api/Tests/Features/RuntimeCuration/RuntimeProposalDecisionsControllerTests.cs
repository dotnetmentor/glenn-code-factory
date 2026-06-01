using System.Security.Claims;
using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Source.Features.Projects.Models;
using Source.Features.RuntimeCuration.Commands;
using Source.Features.RuntimeCuration.Controllers;
using Source.Features.RuntimeCuration.Models;
using Source.Features.RuntimePresets.Contracts;
using Source.Infrastructure;
using Source.Shared.Results;

namespace Api.Tests.Features.RuntimeCuration;

/// <summary>
/// Controller-level coverage for <see cref="RuntimeProposalDecisionsController"/>.
/// Mirrors <see cref="RuntimeProposalsControllerTests"/>'s direct-instantiation
/// rig — mock the mediator, fake an authenticated user via
/// <see cref="DefaultHttpContext"/>, assert the action result + the command
/// dispatched.
///
/// <para>What this file does NOT cover: the JWT bearer middleware (missing /
/// expired token → 401) and the actual handler logic (covered by the per-command
/// handler tests). The 401 here is asserted via the absence of an
/// authenticated principal — the <c>[Authorize]</c> attribute itself is enforced
/// by the framework, so we focus on the controller's branching behavior.</para>
/// </summary>
public class RuntimeProposalDecisionsControllerTests
{
    private readonly Mock<IMediator> _mediator = new();

    /// <summary>
    /// Build a controller wired up to a fresh in-memory DbContext. When
    /// <paramref name="seedOwnedProjectId"/> is non-null, a <see cref="Project"/>
    /// row owned by <paramref name="userId"/> is pre-seeded so the
    /// <c>CallerOwnsProjectAsync</c> gate in each action passes. Tests that want
    /// to exercise the cross-tenant 404 branch pass <c>null</c> / a different
    /// project id.
    /// </summary>
    private RuntimeProposalDecisionsController CreateController(
        string? userId = "user-42",
        Guid? seedOwnedProjectId = null)
    {
        var identity = new ClaimsIdentity(authenticationType: userId is null ? null : "Test");
        if (userId is not null)
        {
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, userId));
        }
        var principal = new ClaimsPrincipal(identity);
        var httpContext = new DefaultHttpContext { User = principal };

        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new ApplicationDbContext(dbOptions);

        if (seedOwnedProjectId is not null && userId is not null)
        {
            db.Projects.Add(new Project
            {
                Id = seedOwnedProjectId.Value,
                WorkspaceId = Guid.NewGuid(),
                OwnerUserId = userId,
                Name = "test-project",
                GithubRepoOwner = "test",
                GithubRepoName = "repo",
                GithubInstallationId = Guid.NewGuid(),
            });
            db.SaveChanges();
        }

        return new RuntimeProposalDecisionsController(_mediator.Object, db)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private static RuntimeProposalDto MakeDto(
        Guid id, Guid projectId, Guid runtimeId, RuntimeProposalStatus status) => new(
            Id: id,
            ProjectId: projectId,
            RuntimeId: runtimeId,
            Status: status,
            ProposedSpec: """{"version":3,"services":[{"kind":"postgres-15","name":"postgres","values":{}}]}""",
            AppliedSpec: status == RuntimeProposalStatus.Rejected
                ? null
                : """{"version":3,"services":[{"kind":"postgres-15","name":"postgres","values":{}}]}""",
            Reason: "test",
            DecidedBy: "user-42",
            DecidedAt: DateTime.UtcNow,
            ErrorMessage: null,
            CreatedAt: DateTime.UtcNow);

    private static RuntimeSpecV3 SimpleV3Spec() => new()
    {
        Services = new List<ServiceInstance>
        {
            new()
            {
                Kind = "postgres-15",
                Name = "postgres",
                Values = new Dictionary<string, JsonElement>(),
            },
        },
    };

    // ----------------------------------------------------------------------
    // Approve
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Approve_HappyPath_Returns200WithDto()
    {
        var projectId = Guid.NewGuid();
        var proposalId = Guid.NewGuid();
        var dto = MakeDto(proposalId, projectId, Guid.NewGuid(), RuntimeProposalStatus.Approved);
        _mediator
            .Setup(m => m.Send(It.IsAny<ApproveProposalCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(dto));

        var controller = CreateController(seedOwnedProjectId: projectId);
        var result = await controller.Approve(projectId, proposalId, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<RuntimeProposalDto>().Subject;
        body.Id.Should().Be(proposalId);
        body.Status.Should().Be(RuntimeProposalStatus.Approved);

        _mediator.Verify(m => m.Send(
            It.Is<ApproveProposalCommand>(c =>
                c.ProjectId == projectId &&
                c.ProposalId == proposalId &&
                c.ActorUserId == "user-42"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Approve_HandlerNotFound_Returns404()
    {
        var projectId = Guid.NewGuid();
        _mediator
            .Setup(m => m.Send(It.IsAny<ApproveProposalCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<RuntimeProposalDto>("not_found"));

        var controller = CreateController(seedOwnedProjectId: projectId);
        var result = await controller.Approve(projectId, Guid.NewGuid(), CancellationToken.None);

        var nf = result.Result.Should().BeOfType<NotFoundObjectResult>().Subject;
        var json = System.Text.Json.JsonSerializer.Serialize(nf.Value);
        json.Should().Contain("\"error\":\"not_found\"");
    }

    [Fact]
    public async Task Approve_HandlerAlreadyDecided_Returns400()
    {
        var projectId = Guid.NewGuid();
        _mediator
            .Setup(m => m.Send(It.IsAny<ApproveProposalCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<RuntimeProposalDto>("already_decided"));

        var controller = CreateController(seedOwnedProjectId: projectId);
        var result = await controller.Approve(projectId, Guid.NewGuid(), CancellationToken.None);

        var br = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var json = System.Text.Json.JsonSerializer.Serialize(br.Value);
        json.Should().Contain("\"error\":\"already_decided\"");
    }

    [Fact]
    public async Task Approve_NoUserIdClaim_ShortCircuitsAtOwnershipGate()
    {
        // Defensive: a malformed principal slipping past [Authorize] (no
        // NameIdentifier claim) is caught by CallerOwnsProjectAsync's null/empty
        // user-id guard — the action returns 404 without ever dispatching to
        // the mediator. This is the safer-than-NRE behavior documented on
        // OwnershipExtensions.
        var controller = CreateController(userId: null);
        var result = await controller.Approve(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
        _mediator.Verify(m => m.Send(It.IsAny<ApproveProposalCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Approve_CallerDoesNotOwnProject_Returns404_WithoutDispatch()
    {
        // Cross-tenant probe: caller is authenticated but the project isn't theirs
        // (or doesn't exist). Uniform 404 to avoid leaking project existence,
        // and the mediator must NOT be invoked (no work done on a non-owned project).
        var controller = CreateController(seedOwnedProjectId: null);
        var result = await controller.Approve(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
        _mediator.Verify(m => m.Send(It.IsAny<ApproveProposalCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ----------------------------------------------------------------------
    // Edit
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Edit_HappyPath_PassesBodyToHandler()
    {
        var projectId = Guid.NewGuid();
        var proposalId = Guid.NewGuid();
        var dto = MakeDto(proposalId, projectId, Guid.NewGuid(), RuntimeProposalStatus.Edited);
        _mediator
            .Setup(m => m.Send(It.IsAny<EditProposalCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(dto));

        var controller = CreateController(seedOwnedProjectId: projectId);
        var editedSpec = SimpleV3Spec();
        var body = new EditProposalRequest(EditedSpec: editedSpec);

        var result = await controller.Edit(projectId, proposalId, body, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        _mediator.Verify(m => m.Send(
            It.Is<EditProposalCommand>(c =>
                c.ProjectId == projectId &&
                c.ProposalId == proposalId &&
                c.EditedSpec == editedSpec &&
                c.ActorUserId == "user-42"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Edit_NullSpec_Returns400_SpecRequired()
    {
        // The controller short-circuits on a missing body before dispatching
        // to the mediator — matches the production "spec_required" guard. The
        // ownership gate runs FIRST, so we still need a seeded project.
        var projectId = Guid.NewGuid();
        var controller = CreateController(seedOwnedProjectId: projectId);
        var body = new EditProposalRequest(EditedSpec: null!);

        var result = await controller.Edit(projectId, Guid.NewGuid(), body, CancellationToken.None);

        var br = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var json = System.Text.Json.JsonSerializer.Serialize(br.Value);
        json.Should().Contain("\"error\":\"spec_required\"");
        _mediator.Verify(m => m.Send(It.IsAny<EditProposalCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Edit_HandlerValidationFailure_Returns400WithErrorBody()
    {
        var projectId = Guid.NewGuid();
        _mediator
            .Setup(m => m.Send(It.IsAny<EditProposalCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<RuntimeProposalDto>("service_name_required"));

        var controller = CreateController(seedOwnedProjectId: projectId);
        var body = new EditProposalRequest(EditedSpec: SimpleV3Spec());

        var result = await controller.Edit(projectId, Guid.NewGuid(), body, CancellationToken.None);

        var br = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var json = System.Text.Json.JsonSerializer.Serialize(br.Value);
        json.Should().Contain("\"error\":\"service_name_required\"");
    }

    // ----------------------------------------------------------------------
    // Reject
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Reject_HappyPath_Returns200WithDto()
    {
        var projectId = Guid.NewGuid();
        var proposalId = Guid.NewGuid();
        var dto = MakeDto(proposalId, projectId, Guid.NewGuid(), RuntimeProposalStatus.Rejected);
        _mediator
            .Setup(m => m.Send(It.IsAny<RejectProposalCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(dto));

        var controller = CreateController(seedOwnedProjectId: projectId);
        var result = await controller.Reject(projectId, proposalId, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<RuntimeProposalDto>().Subject;
        body.Status.Should().Be(RuntimeProposalStatus.Rejected);
    }

    [Fact]
    public async Task Reject_HandlerNotFound_Returns404()
    {
        var projectId = Guid.NewGuid();
        _mediator
            .Setup(m => m.Send(It.IsAny<RejectProposalCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<RuntimeProposalDto>("not_found"));

        var controller = CreateController(seedOwnedProjectId: projectId);
        var result = await controller.Reject(projectId, Guid.NewGuid(), CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }
}

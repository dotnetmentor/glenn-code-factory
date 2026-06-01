using System.Security.Claims;
using System.Text.Json;
using Api.Tests.Infrastructure;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Source.Features.Projects.Models;
using Source.Features.RuntimeCuration.Controllers;
using Source.Features.RuntimeCuration.Models;
using Source.Features.RuntimeCuration.Queries;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.RuntimePresets.Contracts;
using Source.Infrastructure;
using Source.Shared.Results;

namespace Api.Tests.Features.RuntimeCuration;

/// <summary>
/// Controller-level coverage for <see cref="RuntimeProposalsReadController"/>
/// — direct instantiation rig, mock the mediator, fake an authenticated
/// principal. The framework-level <c>[Authorize]</c> guard is exercised by
/// the existing harness in integration tests; here we focus on the
/// branching: success → 200 + typed body, not_found → 404, other failure →
/// 400 with an error envelope.
/// </summary>
public class RuntimeProposalsReadControllerTests : IDisposable
{
    private const string OwnerUserId = "user-42";
    private readonly Mock<IMediator> _mediator = new();
    private readonly ApplicationDbContext _db = TestDbContextFactory.Create();

    /// <summary>
    /// Seeds a Project owned by <see cref="OwnerUserId"/> so the controller's
    /// project-ownership gate (introduced as part of the cross-cutting
    /// project-ownership-gating fix) lets the call through. Returns the
    /// project's id for the controller-call site to thread into the route.
    /// </summary>
    private Guid SeedOwnedProject()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(),
            OwnerUserId = OwnerUserId,
            Name = "test",
            GithubRepoOwner = "test",
            GithubRepoName = "test",
            GithubInstallationId = Guid.NewGuid(),
        };
        _db.Projects.Add(project);
        _db.SaveChanges();
        return project.Id;
    }

    private RuntimeProposalsReadController CreateController()
    {
        var identity = new ClaimsIdentity(authenticationType: "Test");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, OwnerUserId));
        var principal = new ClaimsPrincipal(identity);
        var httpContext = new DefaultHttpContext { User = principal };

        return new RuntimeProposalsReadController(_mediator.Object, _db)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    public void Dispose() => _db.Dispose();

    private static RuntimeProposalDto MakeDto(
        Guid id, Guid projectId, RuntimeProposalStatus status) => new(
            Id: id,
            ProjectId: projectId,
            RuntimeId: Guid.NewGuid(),
            Status: status,
            ProposedSpec: """{"version":3,"services":[]}""",
            AppliedSpec: null,
            Reason: "test",
            DecidedBy: null,
            DecidedAt: null,
            ErrorMessage: null,
            CreatedAt: DateTime.UtcNow);

    // ----------------------------------------------------------------------
    // List
    // ----------------------------------------------------------------------

    [Fact]
    public async Task List_HappyPath_Returns200WithBody()
    {
        var projectId = SeedOwnedProject();
        var dtos = new List<RuntimeProposalDto>
        {
            MakeDto(Guid.NewGuid(), projectId, RuntimeProposalStatus.Pending),
            MakeDto(Guid.NewGuid(), projectId, RuntimeProposalStatus.Approved),
        };
        _mediator
            .Setup(m => m.Send(It.IsAny<ListRuntimeProposalsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(dtos));

        var controller = CreateController();
        var result = await controller.List(
            projectId, status: null, limit: 50, ct: CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeAssignableTo<List<RuntimeProposalDto>>();
        ((List<RuntimeProposalDto>)ok.Value!).Should().HaveCount(2);

        _mediator.Verify(m => m.Send(
            It.Is<ListRuntimeProposalsQuery>(q =>
                q.ProjectId == projectId && q.Status == null && q.Limit == 50),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task List_PassesStatusAndLimitToQuery()
    {
        _mediator
            .Setup(m => m.Send(It.IsAny<ListRuntimeProposalsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new List<RuntimeProposalDto>()));

        var projectId = SeedOwnedProject();
        var controller = CreateController();
        await controller.List(
            projectId,
            status: RuntimeProposalStatus.Pending,
            limit: 10,
            ct: CancellationToken.None);

        _mediator.Verify(m => m.Send(
            It.Is<ListRuntimeProposalsQuery>(q =>
                q.ProjectId == projectId
                && q.Status == RuntimeProposalStatus.Pending
                && q.Limit == 10),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ----------------------------------------------------------------------
    // Get
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Get_HappyPath_Returns200WithDto()
    {
        var projectId = SeedOwnedProject();
        var proposalId = Guid.NewGuid();
        var dto = MakeDto(proposalId, projectId, RuntimeProposalStatus.Pending);
        _mediator
            .Setup(m => m.Send(It.IsAny<GetRuntimeProposalQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(dto));

        var controller = CreateController();
        var result = await controller.Get(projectId, proposalId, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<RuntimeProposalDto>()
            .Which.Id.Should().Be(proposalId);
    }

    [Fact]
    public async Task Get_NotFound_Returns404()
    {
        var projectId = SeedOwnedProject();
        _mediator
            .Setup(m => m.Send(It.IsAny<GetRuntimeProposalQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<RuntimeProposalDto>("not_found"));

        var controller = CreateController();
        var result = await controller.Get(projectId, Guid.NewGuid(), CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    // ----------------------------------------------------------------------
    // GetSpec
    // ----------------------------------------------------------------------

    [Fact]
    public async Task GetSpec_HappyPath_Returns200WithDto()
    {
        var projectId = SeedOwnedProject();
        var dto = new ProjectRuntimeSpecDto(
            RuntimeId: Guid.NewGuid(),
            ProjectId: projectId,
            State: RuntimeState.Online,
            Spec: new RuntimeSpecV3
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
            },
            SpecUpdatedAt: DateTime.UtcNow);
        _mediator
            .Setup(m => m.Send(It.IsAny<GetProjectRuntimeSpecQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(dto));

        var controller = CreateController();
        var result = await controller.GetSpec(projectId, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<ProjectRuntimeSpecDto>()
            .Which.RuntimeId.Should().Be(dto.RuntimeId);
    }

    [Fact]
    public async Task GetSpec_NotFound_Returns404()
    {
        var projectId = SeedOwnedProject();
        _mediator
            .Setup(m => m.Send(It.IsAny<GetProjectRuntimeSpecQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<ProjectRuntimeSpecDto>("not_found"));

        var controller = CreateController();
        var result = await controller.GetSpec(projectId, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }
}

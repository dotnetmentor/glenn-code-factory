using System.Security.Claims;
using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Source.Features.RuntimeCuration.Commands;
using Source.Features.RuntimeCuration.Controllers;
using Source.Features.RuntimePresets.Contracts;
using Source.Features.RuntimeTokens.Models;
using Source.Shared.Results;

namespace Api.Tests.Features.RuntimeCuration;

/// <summary>
/// Unit tests for <see cref="RuntimeProposalsController.Create"/>. We
/// instantiate the controller directly with a mocked <see cref="IMediator"/>
/// and stamp a <see cref="DefaultHttpContext"/> with a <see cref="ClaimsPrincipal"/>
/// carrying the <c>rt_runtime</c> claim — i.e. we test what the controller
/// does <i>after</i> the JWT bearer middleware has authenticated the caller.
///
/// <para>Mirrors <see cref="Api.Tests.Features.Mcp.BootstrapMcpConfigControllerTests"/>:
/// same controller-level claim cross-check pattern, same in-memory rig.</para>
///
/// <para>What this file does NOT cover: the JWT bearer middleware (missing /
/// expired / revoked → 401) and the actual handler logic (covered by
/// <see cref="CreateRuntimeProposalCommandHandlerTests"/>).</para>
/// </summary>
public class RuntimeProposalsControllerTests
{
    private readonly Mock<IMediator> _mediator = new();

    private RuntimeProposalsController CreateController(Guid? claimRuntimeId)
    {
        var identity = new ClaimsIdentity(authenticationType: "RuntimeToken");
        if (claimRuntimeId.HasValue)
        {
            identity.AddClaim(new Claim(RuntimeTokenClaimNames.RuntimeId, claimRuntimeId.Value.ToString()));
        }
        var principal = new ClaimsPrincipal(identity);

        var httpContext = new DefaultHttpContext { User = principal };
        return new RuntimeProposalsController(_mediator.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private static CreateRuntimeProposalRequest ValidBody() => new(
        ProposedSpec: new RuntimeSpecV3
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
        Reason: "needs DB");

    // ----------------------------------------------------------------------

    [Fact]
    public async Task ClaimMismatchesPath_returns_403()
    {
        // Token's rt_runtime is a different (still-valid) Guid than the path —
        // a daemon may only propose for itself. 403, not 401: caller IS
        // authenticated, just unauthorised for this resource.
        var pathRuntimeId = Guid.NewGuid();
        var controller = CreateController(claimRuntimeId: Guid.NewGuid());

        var result = await controller.Create(pathRuntimeId, ValidBody(), CancellationToken.None);

        result.Result.Should().BeOfType<ForbidResult>();
        _mediator.Verify(m => m.Send(It.IsAny<CreateRuntimeProposalCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ClaimMissing_returns_403()
    {
        // Authenticated principal but with NO rt_runtime claim — defensive
        // guard against a malformed principal slipping past auth.
        var pathRuntimeId = Guid.NewGuid();
        var controller = CreateController(claimRuntimeId: null);

        var result = await controller.Create(pathRuntimeId, ValidBody(), CancellationToken.None);

        result.Result.Should().BeOfType<ForbidResult>();
        _mediator.Verify(m => m.Send(It.IsAny<CreateRuntimeProposalCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HappyPath_returns_200_with_proposal_id()
    {
        var runtimeId = Guid.NewGuid();
        var proposalId = Guid.NewGuid();
        _mediator
            .Setup(m => m.Send(It.IsAny<CreateRuntimeProposalCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new CreateRuntimeProposalResponse(proposalId)));

        var controller = CreateController(claimRuntimeId: runtimeId);
        var body = ValidBody();
        var result = await controller.Create(runtimeId, body, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<CreateRuntimeProposalResponse>().Subject;
        payload.ProposalId.Should().Be(proposalId);

        _mediator.Verify(m => m.Send(
            It.Is<CreateRuntimeProposalCommand>(c =>
                c.RuntimeId == runtimeId &&
                c.ProposedSpec == body.ProposedSpec &&
                c.Reason == body.Reason),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandlerNotFound_returns_404_with_error_field()
    {
        var runtimeId = Guid.NewGuid();
        _mediator
            .Setup(m => m.Send(It.IsAny<CreateRuntimeProposalCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<CreateRuntimeProposalResponse>("not_found"));

        var controller = CreateController(claimRuntimeId: runtimeId);
        var result = await controller.Create(runtimeId, ValidBody(), CancellationToken.None);

        var nf = result.Result.Should().BeOfType<NotFoundObjectResult>().Subject;
        // Anonymous payload — assert via reflection-friendly conversion.
        var json = System.Text.Json.JsonSerializer.Serialize(nf.Value);
        json.Should().Contain("\"error\":\"not_found\"");
    }

    [Fact]
    public async Task HandlerValidationFailure_returns_400_with_error_field()
    {
        // V2 dropped the catalog whitelist; structural validation now produces
        // codes like service_name_required / service_command_required /
        // service_name_duplicate.
        var runtimeId = Guid.NewGuid();
        _mediator
            .Setup(m => m.Send(It.IsAny<CreateRuntimeProposalCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<CreateRuntimeProposalResponse>("service_command_required: postgres"));

        var controller = CreateController(claimRuntimeId: runtimeId);
        var result = await controller.Create(runtimeId, ValidBody(), CancellationToken.None);

        var br = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var json = System.Text.Json.JsonSerializer.Serialize(br.Value);
        json.Should().Contain("\"error\":\"service_command_required: postgres\"");
    }
}

using System.Security.Claims;
using Api.Tests.Infrastructure;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Source.Features.ProjectSecrets.Commands;
using Source.Features.ProjectSecrets.Controllers;
using Source.Features.ProjectSecrets.Events;
using Source.Features.ProjectSecrets.Models;
using Source.Features.ProjectSecrets.Queries;
using Source.Features.Projects.Models;
using Source.Infrastructure;

namespace Api.Tests.Features.ProjectSecrets;

/// <summary>
/// Smoke coverage for <see cref="ProjectSecretsController"/>: the reveal happy
/// path through the real query handler, the action-to-mediator wiring on each
/// endpoint, the actor-claim plumbing, and the per-project ownership gate
/// (404 on cross-tenant access, with a <c>CrossTenantDenied</c> audit row
/// written inline).
/// </summary>
public class ProjectSecretsControllerTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();

    /// <summary>Open a fresh DbContext on the shared in-memory store.</summary>
    private ApplicationDbContext OpenDb() => SecretsTestHarness.OpenDb(_dbName);

    /// <summary>
    /// Seed a <see cref="Project"/> owned by <paramref name="ownerUserId"/> and
    /// return its id so tests can exercise the ownership gate. The in-memory
    /// provider doesn't enforce FKs, so we don't need to seed Workspace / User
    /// / GithubInstallation rows just for the gate to find the project.
    /// </summary>
    private async Task<Guid> SeedProjectAsync(string ownerUserId)
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            Name = $"test-project-{Guid.NewGuid():N}",
            GithubRepoOwner = "test-owner",
            GithubRepoName = "test-repo",
            GithubInstallationId = Guid.NewGuid(),
        };
        await using var db = OpenDb();
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return project.Id;
    }

    private ProjectSecretsController BuildController(string actorUserId)
    {
        var (encryption, _) = SecretsTestHarness.Build(_dbName);
        var mediator = new StubMediator(this, encryption);
        var ctx = OpenDb();
        var controller = new ProjectSecretsController(
            mediator, ctx, NullLogger<ProjectSecretsController>.Instance);

        var identity = new ClaimsIdentity(authenticationType: "Test");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, actorUserId));
        var principal = new ClaimsPrincipal(identity);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal },
        };

        return controller;
    }

    [Fact]
    public async Task Add_then_reveal_round_trips_plaintext()
    {
        var projectId = await SeedProjectAsync("alice");
        var controller = BuildController("alice");

        // POST /
        var addResult = await controller.Add(
            projectId,
            new AddSecretRequest("STRIPE_KEY", "sk_live_abc"),
            CancellationToken.None);

        var addStatus = addResult.Result as ObjectResult;
        addStatus.Should().NotBeNull();
        addStatus!.StatusCode.Should().Be(StatusCodes.Status201Created);
        var addPayload = addStatus.Value as AddSecretResponse;
        addPayload.Should().NotBeNull();
        addPayload!.Version.Should().Be(1);

        // GET /{key}/reveal — separate controller because the controller holds
        // its own DbContext per request. Owner reveals their own secret.
        var revealController = BuildController("alice");
        var revealResult = await revealController.Reveal(projectId, "STRIPE_KEY", CancellationToken.None);

        var revealOk = revealResult.Result as OkObjectResult;
        revealOk.Should().NotBeNull();
        var revealPayload = revealOk!.Value as RevealSecretResponse;
        revealPayload.Should().NotBeNull();
        revealPayload!.Key.Should().Be("STRIPE_KEY");
        revealPayload.Plaintext.Should().Be("sk_live_abc");

        // Reveal audit row was written with the revealing actor.
        await using var verify = OpenDb();
        var revealAudit = await verify.SecretAuditEvents
            .SingleAsync(a => a.Action == SecretAuditAction.Revealed);
        revealAudit.Actor.Should().Be("alice");
        revealAudit.SecretKey.Should().Be("STRIPE_KEY");
    }

    [Fact]
    public async Task Add_with_invalid_key_returns_400_with_error_code()
    {
        var projectId = await SeedProjectAsync("alice");
        var controller = BuildController("alice");

        var result = await controller.Add(
            projectId,
            new AddSecretRequest("1INVALID_START", "value"),
            CancellationToken.None);

        var bad = result.Result as BadRequestObjectResult;
        bad.Should().NotBeNull();
        bad!.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Update_missing_key_returns_404()
    {
        var projectId = await SeedProjectAsync("alice");
        var controller = BuildController("alice");

        var result = await controller.Update(
            projectId,
            "API_KEY",
            new UpdateSecretRequest("value"),
            CancellationToken.None);

        var notFound = result.Result as NotFoundObjectResult;
        notFound.Should().NotBeNull();
        notFound!.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task Delete_returns_204_on_success()
    {
        var projectId = await SeedProjectAsync("alice");
        var addController = BuildController("alice");
        var add = await addController.Add(projectId, new AddSecretRequest("API_KEY", "v"), CancellationToken.None);
        ((add.Result as ObjectResult)!.StatusCode).Should().Be(StatusCodes.Status201Created);

        var deleteController = BuildController("alice");
        var result = await deleteController.Delete(projectId, "API_KEY", CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task List_returns_metadata_with_no_audit_rows()
    {
        var projectId = await SeedProjectAsync("alice");
        var add = BuildController("alice");
        await add.Add(projectId, new AddSecretRequest("ONE", "1"), CancellationToken.None);
        await BuildController("alice").Add(projectId, new AddSecretRequest("TWO", "2"), CancellationToken.None);

        var list = BuildController("alice");
        var result = await list.List(projectId, CancellationToken.None);

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        var payload = ok!.Value as List<SecretMetadataDto>;
        payload.Should().NotBeNull();
        payload!.Should().HaveCount(2);
        payload.Select(p => p.Key).Should().BeEquivalentTo(new[] { "ONE", "TWO" });

        // List MUST not produce a List-action audit row — security-noise.
        await using var verify = OpenDb();
        var listAuditCount = await verify.SecretAuditEvents
            .CountAsync(a => a.Action == SecretAuditAction.ListAttempted);
        listAuditCount.Should().Be(0);
    }

    [Fact]
    public async Task Reveal_by_non_owner_returns_404_and_writes_cross_tenant_denied_audit()
    {
        // Alice owns the project and adds a secret.
        var projectId = await SeedProjectAsync("alice");
        var addController = BuildController("alice");
        var add = await addController.Add(
            projectId,
            new AddSecretRequest("STRIPE_KEY", "sk_live_abc"),
            CancellationToken.None);
        ((add.Result as ObjectResult)!.StatusCode).Should().Be(StatusCodes.Status201Created);

        // Bob (different user) tries to reveal — must be denied as 404
        // (NOT 403) so existence is not leaked, with an inline
        // CrossTenantDenied audit row capturing the probe.
        var revealController = BuildController("bob");
        var result = await revealController.Reveal(projectId, "STRIPE_KEY", CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();

        await using var verify = OpenDb();
        var deniedAudit = await verify.SecretAuditEvents
            .SingleAsync(a => a.Action == SecretAuditAction.CrossTenantDenied);
        deniedAudit.Actor.Should().Be("bob");
        deniedAudit.ProjectId.Should().Be(projectId);
        deniedAudit.SecretKey.Should().Be("STRIPE_KEY");

        // The plaintext-revealing audit row must NOT have been written.
        var revealCount = await verify.SecretAuditEvents
            .CountAsync(a => a.Action == SecretAuditAction.Revealed);
        revealCount.Should().Be(0);
    }

    [Fact]
    public async Task List_on_nonexistent_project_returns_404_with_cross_tenant_audit()
    {
        // No project seeded — gate must 404 before reaching the handler and
        // log the probe.
        var controller = BuildController("alice");
        var unknownProjectId = Guid.NewGuid();

        var result = await controller.List(unknownProjectId, CancellationToken.None);
        result.Result.Should().BeOfType<NotFoundResult>();

        await using var verify = OpenDb();
        var deniedAudit = await verify.SecretAuditEvents
            .SingleAsync(a => a.Action == SecretAuditAction.CrossTenantDenied);
        deniedAudit.Actor.Should().Be("alice");
        deniedAudit.ProjectId.Should().Be(unknownProjectId);
    }

    /// <summary>
    /// Thin in-test mediator that hands the controller's <see cref="IRequest{T}"/>
    /// straight to the real handler with a per-call DbContext (so each handler
    /// gets the per-request scope shape it expects in production). Mirrors the
    /// stub used in <see cref="RuntimeTokens.RuntimeTokensAdminControllerTests"/>.
    /// </summary>
    private sealed class StubMediator : IMediator
    {
        private readonly ProjectSecretsControllerTests _outer;
        private readonly Source.Features.ProjectSecrets.Services.SecretEncryptionService _encryption;
        private readonly Mock<IMediator> _innerForHandlers = new();

        public StubMediator(ProjectSecretsControllerTests outer,
            Source.Features.ProjectSecrets.Services.SecretEncryptionService encryption)
        {
            _outer = outer;
            _encryption = encryption;

            // The handlers themselves take an IMediator they call .Publish on
            // for SecretsChanged. The stub mediator below isn't a notification
            // bus; we satisfy the dependency with a no-op moq.
            _innerForHandlers
                .Setup(m => m.Publish(It.IsAny<SecretsChanged>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        }

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            object? resultObj = request switch
            {
                AddSecretCommand cmd => await new AddSecretCommandHandler(
                    _outer.OpenDb(), _encryption, _innerForHandlers.Object,
                    NullLogger<AddSecretCommandHandler>.Instance).Handle(cmd, cancellationToken),
                UpdateSecretCommand cmd => await new UpdateSecretCommandHandler(
                    _outer.OpenDb(), _encryption, _innerForHandlers.Object).Handle(cmd, cancellationToken),
                DeleteSecretCommand cmd => await new DeleteSecretCommandHandler(
                    _outer.OpenDb(), _innerForHandlers.Object).Handle(cmd, cancellationToken),
                ListSecretsQuery q => await new ListSecretsQueryHandler(_outer.OpenDb()).Handle(q, cancellationToken),
                RevealSecretQuery q => await new RevealSecretQueryHandler(_outer.OpenDb(), _encryption).Handle(q, cancellationToken),
                _ => throw new NotSupportedException(
                    $"StubMediator was not configured to dispatch {request.GetType().Name}."),
            };
            return (TResponse)resultObj!;
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification =>
            throw new NotSupportedException();
    }
}

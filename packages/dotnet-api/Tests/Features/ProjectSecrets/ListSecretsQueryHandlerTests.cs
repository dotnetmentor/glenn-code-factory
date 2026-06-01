using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Source.Features.ProjectSecrets.Commands;
using Source.Features.ProjectSecrets.Events;
using Source.Features.ProjectSecrets.Queries;

namespace Api.Tests.Features.ProjectSecrets;

/// <summary>
/// Coverage for <see cref="ListSecretsQueryHandler"/>: returns metadata only
/// (no plaintext / ciphertext / nonce on the DTO surface), excludes
/// soft-deleted rows, scopes results to the requested project, and writes no
/// audit row.
/// </summary>
public class ListSecretsQueryHandlerTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();

    [Fact]
    public async Task Returns_metadata_only_and_excludes_soft_deleted()
    {
        var (encryption, _) = SecretsTestHarness.Build(_dbName);
        var projectId = Guid.NewGuid();
        var otherProject = Guid.NewGuid();
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.Publish(It.IsAny<SecretsChanged>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Seed three keys: two live, one soft-deleted, plus one in a different
        // project to confirm scoping.
        var seedCtx = SecretsTestHarness.OpenDb(_dbName);
        var addHandler = new AddSecretCommandHandler(
            seedCtx, encryption, mediator.Object,
            NullLogger<AddSecretCommandHandler>.Instance);
        await addHandler.Handle(new AddSecretCommand(projectId, "ALPHA", "a", "user-1"), CancellationToken.None);
        await addHandler.Handle(new AddSecretCommand(projectId, "BETA", "b", "user-1"), CancellationToken.None);
        await addHandler.Handle(new AddSecretCommand(projectId, "GAMMA_DELETED", "c", "user-1"), CancellationToken.None);
        await addHandler.Handle(new AddSecretCommand(otherProject, "OTHER", "o", "user-1"), CancellationToken.None);
        await seedCtx.DisposeAsync();

        var deleteCtx = SecretsTestHarness.OpenDb(_dbName);
        await new DeleteSecretCommandHandler(deleteCtx, mediator.Object)
            .Handle(new DeleteSecretCommand(projectId, "GAMMA_DELETED", "user-1"), CancellationToken.None);
        await deleteCtx.DisposeAsync();

        var auditCountBefore = 0;
        await using (var pre = SecretsTestHarness.OpenDb(_dbName))
        {
            auditCountBefore = await pre.SecretAuditEvents.CountAsync();
        }

        await using var ctx = SecretsTestHarness.OpenDb(_dbName);
        var handler = new ListSecretsQueryHandler(ctx);
        var result = await handler.Handle(new ListSecretsQuery(projectId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2, "soft-deleted and other-project keys are filtered out");
        result.Value.Select(s => s.Key).Should().BeEquivalentTo(new[] { "ALPHA", "BETA" });

        // Listing must not write any audit row — the action volume would
        // drown out the security-relevant signals.
        await using var verify = SecretsTestHarness.OpenDb(_dbName);
        var auditCountAfter = await verify.SecretAuditEvents.CountAsync();
        auditCountAfter.Should().Be(auditCountBefore,
            "ListSecretsQuery is intentionally not audited");
    }

    [Fact]
    public async Task Empty_project_returns_empty_list()
    {
        await using var ctx = SecretsTestHarness.OpenDb(_dbName);
        var handler = new ListSecretsQueryHandler(ctx);
        var result = await handler.Handle(
            new ListSecretsQuery(Guid.NewGuid()),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }
}

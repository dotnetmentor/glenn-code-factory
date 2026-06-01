using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Source.Features.ProjectSecrets.Commands;
using Source.Features.ProjectSecrets.Events;
using Source.Features.ProjectSecrets.Models;
using Source.Features.ProjectSecrets.Queries;

namespace Api.Tests.Features.ProjectSecrets;

/// <summary>
/// Coverage for <see cref="RevealSecretQueryHandler"/>: round-trip plaintext,
/// audit row written before decrypt (and committed even if decrypt later
/// throws), missing key returns <c>"not_found"</c>.
/// </summary>
public class RevealSecretQueryHandlerTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();

    [Fact]
    public async Task Round_trip_returns_original_plaintext()
    {
        var (encryption, _) = SecretsTestHarness.Build(_dbName);
        var projectId = Guid.NewGuid();
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.Publish(It.IsAny<SecretsChanged>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Seed.
        var seedCtx = SecretsTestHarness.OpenDb(_dbName);
        await new AddSecretCommandHandler(
                seedCtx, encryption, mediator.Object,
                NullLogger<AddSecretCommandHandler>.Instance)
            .Handle(
                new AddSecretCommand(projectId, "API_KEY", "plaintext-value", "user-1"),
                CancellationToken.None);
        await seedCtx.DisposeAsync();

        await using var ctx = SecretsTestHarness.OpenDb(_dbName);
        var handler = new RevealSecretQueryHandler(ctx, encryption);
        var result = await handler.Handle(
            new RevealSecretQuery(projectId, "API_KEY", "user-2"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Key.Should().Be("API_KEY");
        result.Value.Plaintext.Should().Be("plaintext-value");
    }

    [Fact]
    public async Task Audit_row_is_written_before_decrypt_returns()
    {
        var (encryption, _) = SecretsTestHarness.Build(_dbName);
        var projectId = Guid.NewGuid();
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.Publish(It.IsAny<SecretsChanged>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var seedCtx = SecretsTestHarness.OpenDb(_dbName);
        await new AddSecretCommandHandler(
                seedCtx, encryption, mediator.Object,
                NullLogger<AddSecretCommandHandler>.Instance)
            .Handle(
                new AddSecretCommand(projectId, "API_KEY", "v", "user-1"),
                CancellationToken.None);
        await seedCtx.DisposeAsync();

        await using var ctx = SecretsTestHarness.OpenDb(_dbName);
        var handler = new RevealSecretQueryHandler(ctx, encryption);
        await handler.Handle(
            new RevealSecretQuery(projectId, "API_KEY", "alice"),
            CancellationToken.None);

        await using var verify = SecretsTestHarness.OpenDb(_dbName);
        var revealAudit = await verify.SecretAuditEvents
            .SingleAsync(a => a.Action == SecretAuditAction.Revealed);
        revealAudit.ProjectId.Should().Be(projectId);
        revealAudit.SecretKey.Should().Be("API_KEY");
        revealAudit.Actor.Should().Be("alice");
    }

    [Fact]
    public async Task Missing_key_returns_not_found_and_writes_no_audit_row()
    {
        var (encryption, _) = SecretsTestHarness.Build(_dbName);

        await using var ctx = SecretsTestHarness.OpenDb(_dbName);
        var handler = new RevealSecretQueryHandler(ctx, encryption);
        var result = await handler.Handle(
            new RevealSecretQuery(Guid.NewGuid(), "MISSING", "user-1"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("not_found");

        await using var verify = SecretsTestHarness.OpenDb(_dbName);
        (await verify.SecretAuditEvents.AnyAsync()).Should().BeFalse(
            "missing key short-circuits before the audit write");
    }

    [Fact]
    public async Task Soft_deleted_key_is_invisible_to_reveal()
    {
        var (encryption, _) = SecretsTestHarness.Build(_dbName);
        var projectId = Guid.NewGuid();
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.Publish(It.IsAny<SecretsChanged>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Seed + delete via handlers so we exercise the same path.
        var seedCtx = SecretsTestHarness.OpenDb(_dbName);
        await new AddSecretCommandHandler(
                seedCtx, encryption, mediator.Object,
                NullLogger<AddSecretCommandHandler>.Instance)
            .Handle(new AddSecretCommand(projectId, "API_KEY", "v", "user-1"), CancellationToken.None);
        await seedCtx.DisposeAsync();

        var deleteCtx = SecretsTestHarness.OpenDb(_dbName);
        await new DeleteSecretCommandHandler(deleteCtx, mediator.Object)
            .Handle(new DeleteSecretCommand(projectId, "API_KEY", "user-1"), CancellationToken.None);
        await deleteCtx.DisposeAsync();

        await using var ctx = SecretsTestHarness.OpenDb(_dbName);
        var result = await new RevealSecretQueryHandler(ctx, encryption)
            .Handle(new RevealSecretQuery(projectId, "API_KEY", "user-1"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("not_found");
    }
}

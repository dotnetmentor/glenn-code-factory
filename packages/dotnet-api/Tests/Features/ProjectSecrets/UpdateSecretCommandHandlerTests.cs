using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Source.Features.ProjectSecrets.Commands;
using Source.Features.ProjectSecrets.Events;
using Source.Features.ProjectSecrets.Models;

namespace Api.Tests.Features.ProjectSecrets;

/// <summary>
/// Coverage for <see cref="UpdateSecretCommandHandler"/>: happy-path version
/// bump + ciphertext rotation, missing key returns <c>"not_found"</c>, audit
/// row written, SecretsChanged published.
/// </summary>
public class UpdateSecretCommandHandlerTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();

    [Fact]
    public async Task Happy_path_bumps_version_and_rotates_ciphertext()
    {
        var (encryption, _) = SecretsTestHarness.Build(_dbName);
        var projectId = Guid.NewGuid();

        // Seed via Add so we go through the same write path the user would.
        var addCtx = SecretsTestHarness.OpenDb(_dbName);
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.Publish(It.IsAny<SecretsChanged>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var addHandler = new AddSecretCommandHandler(
            addCtx, encryption, mediator.Object,
            NullLogger<AddSecretCommandHandler>.Instance);
        var added = await addHandler.Handle(
            new AddSecretCommand(projectId, "API_KEY", "first", "user-1"),
            CancellationToken.None);
        added.IsSuccess.Should().BeTrue();
        await addCtx.DisposeAsync();

        byte[] originalCiphertext;
        await using (var pre = SecretsTestHarness.OpenDb(_dbName))
        {
            originalCiphertext = (await pre.ProjectSecrets
                .AsNoTracking()
                .SingleAsync(s => s.Id == added.Value.SecretId)).Ciphertext;
        }

        var updateCtx = SecretsTestHarness.OpenDb(_dbName);
        var updateHandler = new UpdateSecretCommandHandler(
            updateCtx, encryption, mediator.Object);
        var result = await updateHandler.Handle(
            new UpdateSecretCommand(projectId, "API_KEY", "second", "user-2"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Version.Should().Be(2);
        await updateCtx.DisposeAsync();

        await using var verify = SecretsTestHarness.OpenDb(_dbName);
        var stored = await verify.ProjectSecrets
            .SingleAsync(s => s.Id == added.Value.SecretId);
        stored.Version.Should().Be(2);
        stored.Ciphertext.Should().NotEqual(originalCiphertext,
            "rotation must produce a fresh AEAD output");

        var audits = await verify.SecretAuditEvents
            .Where(a => a.SecretId == added.Value.SecretId)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync();
        audits.Should().HaveCount(2);
        audits[0].Action.Should().Be(SecretAuditAction.Created);
        audits[1].Action.Should().Be(SecretAuditAction.Updated);
        audits[1].Actor.Should().Be("user-2");

        mediator.Verify(
            m => m.Publish(
                It.Is<SecretsChanged>(e =>
                    e.ProjectId == projectId
                    && e.ChangedKey == "API_KEY"
                    && e.Deleted == false),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2)); // once for Add, once for Update
    }

    [Fact]
    public async Task Not_found_returns_failure_with_not_found_code()
    {
        var (encryption, _) = SecretsTestHarness.Build(_dbName);
        await using var ctx = SecretsTestHarness.OpenDb(_dbName);
        var handler = new UpdateSecretCommandHandler(
            ctx, encryption, Mock.Of<IMediator>());

        var result = await handler.Handle(
            new UpdateSecretCommand(Guid.NewGuid(), "API_KEY", "value", "user-1"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("not_found");
    }

    [Fact]
    public async Task Invalid_key_format_short_circuits_before_lookup()
    {
        var (encryption, _) = SecretsTestHarness.Build(_dbName);
        await using var ctx = SecretsTestHarness.OpenDb(_dbName);
        var handler = new UpdateSecretCommandHandler(
            ctx, encryption, Mock.Of<IMediator>());

        var result = await handler.Handle(
            new UpdateSecretCommand(Guid.NewGuid(), "1INVALID_START", "value", "user-1"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("invalid_key_format");
    }

    [Fact]
    public async Task Multiline_plaintext_is_rejected()
    {
        var (encryption, _) = SecretsTestHarness.Build(_dbName);
        await using var ctx = SecretsTestHarness.OpenDb(_dbName);
        var handler = new UpdateSecretCommandHandler(
            ctx, encryption, Mock.Of<IMediator>());

        var result = await handler.Handle(
            new UpdateSecretCommand(Guid.NewGuid(), "API_KEY", "with\nnewline", "user-1"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("invalid_plaintext");
    }
}

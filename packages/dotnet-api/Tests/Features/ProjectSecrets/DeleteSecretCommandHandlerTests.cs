using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Source.Features.ProjectSecrets.Commands;
using Source.Features.ProjectSecrets.Events;
using Source.Features.ProjectSecrets.Models;

namespace Api.Tests.Features.ProjectSecrets;

/// <summary>
/// Coverage for <see cref="DeleteSecretCommandHandler"/>: soft-delete flips
/// IsDeleted and stamps DeletedAt via the interceptor, audit row written,
/// SecretsChanged published with <c>Deleted = true</c>, missing key returns
/// <c>"not_found"</c>.
/// </summary>
public class DeleteSecretCommandHandlerTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();

    [Fact]
    public async Task Happy_path_soft_deletes_and_publishes_event()
    {
        var (encryption, _) = SecretsTestHarness.Build(_dbName);
        var projectId = Guid.NewGuid();
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.Publish(It.IsAny<SecretsChanged>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Seed
        var seedCtx = SecretsTestHarness.OpenDb(_dbName);
        var addHandler = new AddSecretCommandHandler(
            seedCtx, encryption, mediator.Object,
            NullLogger<AddSecretCommandHandler>.Instance);
        var added = await addHandler.Handle(
            new AddSecretCommand(projectId, "GOOG_KEY", "value", "user-1"),
            CancellationToken.None);
        added.IsSuccess.Should().BeTrue();
        await seedCtx.DisposeAsync();

        // Act
        await using var deleteCtx = SecretsTestHarness.OpenDb(_dbName);
        var deleteHandler = new DeleteSecretCommandHandler(deleteCtx, mediator.Object);
        var result = await deleteHandler.Handle(
            new DeleteSecretCommand(projectId, "GOOG_KEY", "user-2"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        // The default query filter hides soft-deleted rows; bypass it to see
        // the row + verify the soft-delete fields landed via the interceptor.
        await using var verify = SecretsTestHarness.OpenDb(_dbName);
        var stored = await verify.ProjectSecrets
            .IgnoreQueryFilters()
            .SingleAsync(s => s.Id == added.Value.SecretId);
        stored.IsDeleted.Should().BeTrue();
        stored.DeletedAt.Should().NotBeNull();

        var audits = await verify.SecretAuditEvents
            .Where(a => a.SecretId == added.Value.SecretId)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync();
        audits.Should().HaveCount(2);
        audits[0].Action.Should().Be(SecretAuditAction.Created);
        audits[1].Action.Should().Be(SecretAuditAction.Deleted);
        audits[1].Actor.Should().Be("user-2");

        mediator.Verify(
            m => m.Publish(
                It.Is<SecretsChanged>(e =>
                    e.ProjectId == projectId
                    && e.ChangedKey == "GOOG_KEY"
                    && e.Deleted == true),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Missing_key_returns_not_found()
    {
        await using var ctx = SecretsTestHarness.OpenDb(_dbName);
        var handler = new DeleteSecretCommandHandler(ctx, Mock.Of<IMediator>());

        var result = await handler.Handle(
            new DeleteSecretCommand(Guid.NewGuid(), "MISSING_KEY", "user-1"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("not_found");
    }

    [Fact]
    public async Task Already_deleted_key_returns_not_found_due_to_query_filter()
    {
        var (encryption, _) = SecretsTestHarness.Build(_dbName);
        var projectId = Guid.NewGuid();
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.Publish(It.IsAny<SecretsChanged>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Seed + delete
        var seedCtx = SecretsTestHarness.OpenDb(_dbName);
        await new AddSecretCommandHandler(
                seedCtx, encryption, mediator.Object,
                NullLogger<AddSecretCommandHandler>.Instance)
            .Handle(new AddSecretCommand(projectId, "ONCE", "v", "user-1"), CancellationToken.None);
        await seedCtx.DisposeAsync();

        var firstDeleteCtx = SecretsTestHarness.OpenDb(_dbName);
        await new DeleteSecretCommandHandler(firstDeleteCtx, mediator.Object)
            .Handle(new DeleteSecretCommand(projectId, "ONCE", "user-1"), CancellationToken.None);
        await firstDeleteCtx.DisposeAsync();

        // Second delete — soft-deleted row is hidden by the global filter,
        // so the handler reads "not_found".
        await using var secondCtx = SecretsTestHarness.OpenDb(_dbName);
        var second = await new DeleteSecretCommandHandler(secondCtx, mediator.Object)
            .Handle(new DeleteSecretCommand(projectId, "ONCE", "user-1"), CancellationToken.None);
        second.IsSuccess.Should().BeFalse();
        second.Error.Should().Be("not_found");
    }

    [Fact]
    public async Task Invalid_key_format_short_circuits()
    {
        await using var ctx = SecretsTestHarness.OpenDb(_dbName);
        var handler = new DeleteSecretCommandHandler(ctx, Mock.Of<IMediator>());

        var result = await handler.Handle(
            new DeleteSecretCommand(Guid.NewGuid(), "lower", "user-1"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("invalid_key_format");
    }
}

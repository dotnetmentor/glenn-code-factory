using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Source.Features.RuntimeLifecycle.Events;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.EventHandlers;
using Source.Features.SignalR.Hubs;
using Source.Infrastructure;

namespace Api.Tests.Features.SignalR;

/// <summary>
/// Unit tests for <see cref="BroadcastRuntimeStateChangedHandler"/>. We mock the
/// typed <see cref="IHubContext{THub, T}"/> end-to-end so we can verify two
/// things the spec cares about:
///
/// <list type="bullet">
///   <item>the broadcast is scoped to the correct <c>project-{id}</c> group;</item>
///   <item>the wire payload uses string state values (not ordinal ints) so the
///         JS client sees stable names;</item>
///   <item>hub failures are swallowed — broadcast must not poison the domain
///         event dispatcher chain (audit row is written by a sibling handler
///         and must not roll back).</item>
/// </list>
/// </summary>
public class BroadcastRuntimeStateChangedHandlerTests
{
    private readonly Mock<IHubClients<IAgentClient>> _clients = new();
    private readonly Mock<IHubContext<AgentHub, IAgentClient>> _hub = new();
    private readonly Mock<IAgentClient> _groupClient = new();

    public BroadcastRuntimeStateChangedHandlerTests()
    {
        _hub.SetupGet(h => h.Clients).Returns(_clients.Object);
        _clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupClient.Object);
    }

    /// <summary>
    /// Bare in-memory DbContext for the handler's workspace-resolution lookup.
    /// We don't seed any Project row, so the additive
    /// <c>workspace-{workspaceId}</c> broadcast short-circuits on a null
    /// workspace and the existing project-group assertions stay untouched.
    /// Tests that specifically care about the workspace fan-out seed their own
    /// Project row before invoking the handler.
    /// </summary>
    private static ApplicationDbContext NewDb() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task Handle_broadcasts_to_project_group_with_correct_payload()
    {
        var runtimeId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var occurredAt = new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc);
        var domainEvent = new RuntimeStateChanged(
            RuntimeId: runtimeId,
            ProjectId: projectId,
            BranchId: branchId,
            FromState: RuntimeState.Booting,
            ToState: RuntimeState.Bootstrapping,
            Reason: "fly_webhook:machine.started",
            TriggeredBy: "fly:webhook",
            Metadata: null,
            OccurredAt: occurredAt);

        RuntimeStateChangedNotification? captured = null;
        _groupClient
            .Setup(c => c.RuntimeStateChanged(It.IsAny<RuntimeStateChangedNotification>()))
            .Callback<RuntimeStateChangedNotification>(p => captured = p)
            .Returns(Task.CompletedTask);

        using var db = NewDb();
        var handler = new BroadcastRuntimeStateChangedHandler(_hub.Object, db, NullLogger<BroadcastRuntimeStateChangedHandler>.Instance);

        await handler.Handle(domainEvent, CancellationToken.None);

        // Group selection — the live broadcast is branch-scoped (the per-branch
        // chat tab subscribes to branch-{id}). No Project row is seeded, so the
        // additive workspace-{id} fan-out short-circuits and branch is the only
        // group touched.
        _clients.Verify(c => c.Group($"branch-{branchId}"), Times.Once);
        _clients.Verify(c => c.Group(It.Is<string>(s => s != $"branch-{branchId}")), Times.Never);

        // Single broadcast with the mapped payload.
        _groupClient.Verify(c => c.RuntimeStateChanged(It.IsAny<RuntimeStateChangedNotification>()), Times.Once);
        captured.Should().NotBeNull();
        captured!.RuntimeId.Should().Be(runtimeId);
        captured.ProjectId.Should().Be(projectId);
        captured.FromState.Should().Be("Booting");
        captured.ToState.Should().Be("Bootstrapping");
        captured.Reason.Should().Be("fly_webhook:machine.started");
        captured.ChangedAt.Should().Be(occurredAt);
    }

    [Fact]
    public async Task Handle_uses_string_representation_of_states()
    {
        // This is the wire-stability guarantee: JS clients see "Online" / "Bootstrapping",
        // not the ordinal int. Re-ordering the enum on the backend must not silently
        // break the client.
        var domainEvent = new RuntimeStateChanged(
            runtimeId: Guid.NewGuid(),
            projectId: Guid.NewGuid(),
            branchId: Guid.NewGuid(),
            fromState: RuntimeState.Bootstrapping,
            toState: RuntimeState.Online,
            reason: "bootstrap_complete",
            triggeredBy: "daemon");

        RuntimeStateChangedNotification? captured = null;
        _groupClient
            .Setup(c => c.RuntimeStateChanged(It.IsAny<RuntimeStateChangedNotification>()))
            .Callback<RuntimeStateChangedNotification>(p => captured = p)
            .Returns(Task.CompletedTask);

        using var db = NewDb();
        var handler = new BroadcastRuntimeStateChangedHandler(_hub.Object, db, NullLogger<BroadcastRuntimeStateChangedHandler>.Instance);

        await handler.Handle(domainEvent, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.FromState.Should().Be("Bootstrapping");
        captured.ToState.Should().Be("Online");
    }

    [Fact]
    public async Task Handle_maps_null_FromState_to_null()
    {
        // The very first transition for a new runtime has no prior state — the
        // event's FromState is nullable and the wire payload preserves that nullability.
        var domainEvent = new RuntimeStateChanged(
            runtimeId: Guid.NewGuid(),
            projectId: Guid.NewGuid(),
            branchId: Guid.NewGuid(),
            fromState: null,
            toState: RuntimeState.Pending,
            reason: "runtime_provisioned",
            triggeredBy: "system");

        RuntimeStateChangedNotification? captured = null;
        _groupClient
            .Setup(c => c.RuntimeStateChanged(It.IsAny<RuntimeStateChangedNotification>()))
            .Callback<RuntimeStateChangedNotification>(p => captured = p)
            .Returns(Task.CompletedTask);

        using var db = NewDb();
        var handler = new BroadcastRuntimeStateChangedHandler(_hub.Object, db, NullLogger<BroadcastRuntimeStateChangedHandler>.Instance);

        await handler.Handle(domainEvent, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.FromState.Should().BeNull();
        captured.ToState.Should().Be("Pending");
    }

    [Fact]
    public async Task Handle_swallows_hub_exception()
    {
        // Critical reliability invariant: SignalR broadcast failures must not
        // propagate. The audit row is already written by PersistRuntimeStateEventHandler
        // and an exception here would surface back into the domain dispatcher,
        // potentially abort downstream handlers, and confuse callers about whether
        // the state transition itself failed.
        _groupClient
            .Setup(c => c.RuntimeStateChanged(It.IsAny<RuntimeStateChangedNotification>()))
            .ThrowsAsync(new InvalidOperationException("hub is gone"));

        var logger = new Mock<ILogger<BroadcastRuntimeStateChangedHandler>>();
        using var db = NewDb();
        var handler = new BroadcastRuntimeStateChangedHandler(_hub.Object, db, logger.Object);

        var domainEvent = new RuntimeStateChanged(
            runtimeId: Guid.NewGuid(),
            projectId: Guid.NewGuid(),
            branchId: Guid.NewGuid(),
            fromState: RuntimeState.Online,
            toState: RuntimeState.Crashed,
            reason: "fly_webhook:machine.crashed",
            triggeredBy: "fly:webhook");

        // Act — must not throw.
        var act = async () => await handler.Handle(domainEvent, CancellationToken.None);
        await act.Should().NotThrowAsync();

        // We logged the failure at Warning so an operator can see broadcast loss.
        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<InvalidOperationException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}

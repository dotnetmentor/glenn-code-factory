using Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeLifecycle.EventHandlers;
using Source.Features.RuntimeLifecycle.Events;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;

namespace Api.Tests.Features.RuntimeLifecycle;

/// <summary>
/// Behavioural tests for the rich <see cref="ProjectRuntime.TransitionTo"/>
/// method and its interplay with the <see cref="RuntimeStateChanged"/>
/// domain event + <see cref="PersistRuntimeStateEventHandler"/> audit writer.
///
/// <para>The default <see cref="HandlerTestBase"/>-supplied
/// <see cref="ApplicationDbContext"/> is wired with the canonical
/// <c>DomainEventInterceptor</c> + MediatR pipeline (Card 4 of the
/// runtime-health spec consolidated this — see <see cref="TestDbContextFactory"/>),
/// so all this test class needs to do is seed a runtime and call
/// <c>TransitionTo</c> + <c>SaveChanges</c>. The interceptor dispatches the
/// raised event through MediatR, the auto-discovered audit handler writes
/// the row, and the test asserts on it.</para>
/// </summary>
public class ProjectRuntimeTransitionTests : HandlerTestBase
{
    private ProjectRuntime SeedRuntime(RuntimeState state = RuntimeState.Pending, int respawnRetries = 0)
    {
        var runtime = new ProjectRuntime
        {
            ProjectId = Guid.NewGuid(),
            Region = "arn",
            State = state,
            RespawnRetries = respawnRetries,
        };
        Context.ProjectRuntimes.Add(runtime);
        Context.SaveChanges();
        return runtime;
    }

    [Fact]
    public async Task TransitionTo_legal_succeeds_and_records_event()
    {
        var runtime = SeedRuntime(RuntimeState.Pending);

        var result = runtime.TransitionTo(
            RuntimeState.Booting,
            reason: "provisioner:requested_boot",
            triggeredBy: "system:provisioner");

        result.IsSuccess.Should().BeTrue();
        await Context.SaveChangesAsync();

        // State on the runtime row was updated.
        runtime.State.Should().Be(RuntimeState.Booting);

        // The audit handler should have inserted exactly one row matching the transition.
        var events = await Context.RuntimeStateEvents
            .Where(e => e.RuntimeId == runtime.Id)
            .ToListAsync();

        events.Should().HaveCount(1);
        var audit = events.Single();
        audit.FromState.Should().Be(RuntimeState.Pending);
        audit.ToState.Should().Be(RuntimeState.Booting);
        audit.Reason.Should().Be("provisioner:requested_boot");
        audit.TriggeredBy.Should().Be("system:provisioner");
        audit.Metadata.Should().BeNull();
    }

    [Fact]
    public async Task TransitionTo_illegal_returns_Failure_and_does_not_persist_event()
    {
        // Online -> Pending is not a legal edge in the state graph.
        var runtime = SeedRuntime(RuntimeState.Online);

        var result = runtime.TransitionTo(
            RuntimeState.Pending,
            reason: "operator:bogus",
            triggeredBy: "user:abc");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Illegal transition");

        // State must be untouched.
        runtime.State.Should().Be(RuntimeState.Online);

        // Save changes anyway — there should be nothing relevant to commit and
        // critically no domain event should have been queued, so no audit row.
        await Context.SaveChangesAsync();

        var events = await Context.RuntimeStateEvents
            .Where(e => e.RuntimeId == runtime.Id)
            .ToListAsync();
        events.Should().BeEmpty("illegal transitions must not raise an event or write an audit row");
    }

    [Fact]
    public async Task TransitionTo_to_Online_resets_RespawnRetries()
    {
        var runtime = SeedRuntime(RuntimeState.Bootstrapping, respawnRetries: 2);

        var result = runtime.TransitionTo(
            RuntimeState.Online,
            reason: "daemon:runtime_ready",
            triggeredBy: "system:bootstrap_daemon");

        result.IsSuccess.Should().BeTrue();
        await Context.SaveChangesAsync();

        runtime.State.Should().Be(RuntimeState.Online);
        runtime.RespawnRetries.Should().Be(0, "reaching Online wipes the retry counter");
    }

    [Fact]
    public async Task TransitionTo_with_metadata_persists_metadata()
    {
        var runtime = SeedRuntime(RuntimeState.Pending);
        const string metadata = "{\"hint\":\"x\"}";

        var result = runtime.TransitionTo(
            RuntimeState.Booting,
            reason: "provisioner:requested_boot",
            triggeredBy: "system:provisioner",
            metadata: metadata);

        result.IsSuccess.Should().BeTrue();
        await Context.SaveChangesAsync();

        var audit = await Context.RuntimeStateEvents
            .SingleAsync(e => e.RuntimeId == runtime.Id);
        audit.Metadata.Should().Be(metadata, "metadata blob round-trips through the event into the audit row");
    }

    [Fact]
    public async Task TransitionTo_records_initial_FromState_correctly_when_running_multiple()
    {
        var runtime = SeedRuntime(RuntimeState.Pending);

        // Step 1: Pending -> Booting
        var step1 = runtime.TransitionTo(
            RuntimeState.Booting,
            reason: "provisioner:requested_boot",
            triggeredBy: "system:provisioner");
        step1.IsSuccess.Should().BeTrue();
        await Context.SaveChangesAsync();

        // Step 2: Booting -> Bootstrapping
        var step2 = runtime.TransitionTo(
            RuntimeState.Bootstrapping,
            reason: "fly:machine_started",
            triggeredBy: "fly:webhook");
        step2.IsSuccess.Should().BeTrue();
        await Context.SaveChangesAsync();

        runtime.State.Should().Be(RuntimeState.Bootstrapping);

        var events = await Context.RuntimeStateEvents
            .Where(e => e.RuntimeId == runtime.Id)
            .OrderBy(e => e.CreatedAt)
            .ThenBy(e => e.FromState)
            .ToListAsync();

        events.Should().HaveCount(2);

        var firstEvent = events.Single(e => e.ToState == RuntimeState.Booting);
        firstEvent.FromState.Should().Be(RuntimeState.Pending);

        var secondEvent = events.Single(e => e.ToState == RuntimeState.Bootstrapping);
        secondEvent.FromState.Should().Be(RuntimeState.Booting);
    }

    [Fact]
    public async Task TransitionTo_to_Booting_clears_stale_LastBootstrapActivityAt()
    {
        var runtime = SeedRuntime(RuntimeState.Crashed);
        runtime.LastBootstrapActivityAt = DateTime.UtcNow.AddHours(-5);
        await Context.SaveChangesAsync();

        var result = runtime.TransitionTo(
            RuntimeState.Booting,
            reason: "respawn:created",
            triggeredBy: "system:respawn");

        result.IsSuccess.Should().BeTrue();
        runtime.LastBootstrapActivityAt.Should().BeNull(
            "a fresh boot must not inherit bootstrap activity from a prior Online run");
    }

    [Fact]
    public async Task TransitionTo_to_Bootstrapping_preserves_LastBootstrapActivityAt()
    {
        var runtime = SeedRuntime(RuntimeState.Booting);
        var activityAt = DateTime.UtcNow.AddMinutes(-2);
        runtime.LastBootstrapActivityAt = activityAt;
        await Context.SaveChangesAsync();

        var result = runtime.TransitionTo(
            RuntimeState.Bootstrapping,
            reason: "daemon:connected",
            triggeredBy: "system:bootstrap_daemon");

        result.IsSuccess.Should().BeTrue();
        runtime.LastBootstrapActivityAt.Should().Be(activityAt,
            "mid-boot progress must keep the activity timestamp set by bootstrap events");
    }

    [Fact]
    public async Task Restart_clears_stale_LastBootstrapActivityAt()
    {
        var runtime = SeedRuntime(RuntimeState.Failed);
        runtime.LastBootstrapActivityAt = DateTime.UtcNow.AddHours(-5);
        await Context.SaveChangesAsync();

        var result = runtime.Restart(Guid.NewGuid());

        result.IsSuccess.Should().BeTrue();
        runtime.State.Should().Be(RuntimeState.Pending);
        runtime.LastBootstrapActivityAt.Should().BeNull();
    }

    [Fact]
    public async Task Restart_fromOnline_succeeds()
    {
        var runtime = SeedRuntime(RuntimeState.Online);
        await Context.SaveChangesAsync();

        var result = runtime.Restart(Guid.NewGuid());

        result.IsSuccess.Should().BeTrue();
        runtime.State.Should().Be(RuntimeState.Pending);
    }
}

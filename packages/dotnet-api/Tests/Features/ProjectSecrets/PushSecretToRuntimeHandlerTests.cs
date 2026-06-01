using Api.Tests.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Source.Features.Health.Services;
using Source.Features.ProjectSecrets.Events;
using Source.Features.ProjectSecrets.EventHandlers;
using Source.Features.ProjectSecrets.Models;
using Source.Features.ProjectSecrets.Services;
using Source.Features.RuntimeCuration.Services;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;
using Source.Infrastructure;

namespace Api.Tests.Features.ProjectSecrets;

/// <summary>
/// Coverage for <see cref="PushSecretToRuntimeHandler"/> — the SignalR fan-out
/// that delivers env-var deltas to a project's running daemon when a secret
/// changes.
///
/// <list type="bullet">
///   <item>online runtime + add → upsert delta (key, plaintext);</item>
///   <item>online runtime + delete → delete delta (key, null);</item>
///   <item>no online runtime → no SignalR call, info-log captured;</item>
///   <item>multiple non-deleted runtimes → most-recent (by CreatedAt) wins
///         the targeting decision, mirroring <c>RuntimeStatusController</c>.</item>
/// </list>
///
/// <para>Hub primitives are mocked end-to-end the way
/// <c>BroadcastRuntimeStateChangedHandlerTests</c> does. The real
/// <see cref="ApplicationDbContext"/> on InMemory + the real
/// <c>SecretEncryptionService</c> wire the decrypt path so the assertion
/// "the delta carries the original plaintext" exercises the full
/// encrypt-then-decrypt round-trip.</para>
/// </summary>
public class PushSecretToRuntimeHandlerTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();

    private readonly Mock<IHubClients<IRuntimeClient>> _clients = new();
    private readonly Mock<IHubContext<RuntimeHub, IRuntimeClient>> _hub = new();
    private readonly Mock<IRuntimeClient> _groupClient = new();
    private readonly Mock<ICurrentExpandedSpecResolver> _resolver = new();
    private readonly RestartServiceThrottle _throttle = new();
    private readonly FakeClock _clock = new();

    public PushSecretToRuntimeHandlerTests()
    {
        _hub.SetupGet(h => h.Clients).Returns(_clients.Object);
        _clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupClient.Object);

        // Default: no current expanded spec → restart-on-fill is a no-op. Tests
        // that exercise restart-on-fill override this to return a V2 JSON body.
        _resolver
            .Setup(r => r.ResolveAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
    }

    /// <summary>
    /// Construct the handler with the shared mocked hub + resolver + throttle +
    /// clock. <paramref name="logger"/> defaults to the null logger; the
    /// no-runtime test passes a Moq logger so it can assert on the info-log.
    /// </summary>
    private PushSecretToRuntimeHandler BuildHandler(
        ApplicationDbContext db,
        SecretEncryptionService encryption,
        ILogger<PushSecretToRuntimeHandler>? logger = null) =>
        new(
            _hub.Object,
            db,
            encryption,
            _resolver.Object,
            _throttle,
            _clock,
            logger ?? NullLogger<PushSecretToRuntimeHandler>.Instance);

    [Fact]
    public async Task Online_runtime_with_add_pushes_upsert_delta_with_plaintext()
    {
        var (encryption, _) = SecretsTestHarness.Build(_dbName);
        var projectId = Guid.NewGuid();
        var runtimeId = Guid.NewGuid();

        // Arrange: live ProjectRuntime in Online state + a ProjectSecret row
        // whose ciphertext was produced by the same encryption service.
        await using (var seed = SecretsTestHarness.OpenDb(_dbName))
        {
            seed.ProjectRuntimes.Add(new ProjectRuntime
            {
                Id = runtimeId,
                ProjectId = projectId,
                State = RuntimeState.Online,
                Region = "arn",
                CreatedAt = DateTime.UtcNow,
                StateChangedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var (ciphertext, nonce, dekVersion) = await encryption.EncryptAsync(
            projectId, "sk_live_abc", CancellationToken.None);

        await using (var seed = SecretsTestHarness.OpenDb(_dbName))
        {
            seed.ProjectSecrets.Add(new ProjectSecret
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                Key = "STRIPE_API_KEY",
                Ciphertext = ciphertext,
                Nonce = nonce,
                DekVersion = dekVersion,
                Version = 1,
                CreatedBy = "user-1",
            });
            await seed.SaveChangesAsync();
        }

        ConfigUpdatePayload? captured = null;
        _groupClient
            .Setup(c => c.UpdateConfig(It.IsAny<ConfigUpdatePayload>()))
            .Callback<ConfigUpdatePayload>(p => captured = p)
            .Returns(Task.CompletedTask);

        await using var ctx = SecretsTestHarness.OpenDb(_dbName);
        var handler = BuildHandler(ctx, encryption);

        // Act
        await handler.Handle(
            new SecretsChanged(projectId, "STRIPE_API_KEY", Deleted: false, BranchId: null),
            CancellationToken.None);

        // Assert: targeted at runtime-{Id}, single upsert delta with plaintext.
        _clients.Verify(c => c.Group($"runtime-{runtimeId}"), Times.Once);
        _clients.Verify(c => c.Group(It.Is<string>(s => s != $"runtime-{runtimeId}")), Times.Never);
        _groupClient.Verify(c => c.UpdateConfig(It.IsAny<ConfigUpdatePayload>()), Times.Once);

        captured.Should().NotBeNull();
        captured!.RuntimeId.Should().Be(runtimeId);
        captured.RuntimeToken.Should().BeNull();
        captured.HooksJson.Should().BeNull();
        captured.AutoCommit.Should().BeNull();
        captured.DeployKey.Should().BeNull();
        captured.EnvVarsDelta.Should().NotBeNull();
        captured.EnvVarsDelta!.Should().HaveCount(1);
        captured.EnvVarsDelta[0].Key.Should().Be("STRIPE_API_KEY");
        captured.EnvVarsDelta[0].Value.Should().Be("sk_live_abc");
    }

    [Fact]
    public async Task Online_runtime_with_delete_pushes_null_value_delta()
    {
        var (encryption, _) = SecretsTestHarness.Build(_dbName);
        var projectId = Guid.NewGuid();
        var runtimeId = Guid.NewGuid();

        await using (var seed = SecretsTestHarness.OpenDb(_dbName))
        {
            seed.ProjectRuntimes.Add(new ProjectRuntime
            {
                Id = runtimeId,
                ProjectId = projectId,
                State = RuntimeState.Online,
                Region = "arn",
                CreatedAt = DateTime.UtcNow,
                StateChangedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        ConfigUpdatePayload? captured = null;
        _groupClient
            .Setup(c => c.UpdateConfig(It.IsAny<ConfigUpdatePayload>()))
            .Callback<ConfigUpdatePayload>(p => captured = p)
            .Returns(Task.CompletedTask);

        await using var ctx = SecretsTestHarness.OpenDb(_dbName);
        var handler = BuildHandler(ctx, encryption);

        // Act
        await handler.Handle(
            new SecretsChanged(projectId, "OBSOLETE_KEY", Deleted: true, BranchId: null),
            CancellationToken.None);

        // Assert: delta is (key, null). The handler must NOT consult the
        // ProjectSecret row on delete — the row is already soft-deleted and
        // hidden by the global query filter, so a lookup would miss anyway.
        _groupClient.Verify(c => c.UpdateConfig(It.IsAny<ConfigUpdatePayload>()), Times.Once);
        captured.Should().NotBeNull();
        captured!.EnvVarsDelta.Should().NotBeNull();
        captured.EnvVarsDelta!.Should().HaveCount(1);
        captured.EnvVarsDelta[0].Key.Should().Be("OBSOLETE_KEY");
        captured.EnvVarsDelta[0].Value.Should().BeNull();
    }

    [Fact]
    public async Task No_runtime_swallows_event_and_logs_info()
    {
        var (encryption, _) = SecretsTestHarness.Build(_dbName);
        var logger = new Mock<ILogger<PushSecretToRuntimeHandler>>();

        await using var ctx = SecretsTestHarness.OpenDb(_dbName);
        var handler = BuildHandler(ctx, encryption, logger.Object);

        // Act — no ProjectRuntime row at all.
        await handler.Handle(
            new SecretsChanged(Guid.NewGuid(), "ANY_KEY", Deleted: false, BranchId: null),
            CancellationToken.None);

        // No SignalR fan-out.
        _clients.Verify(c => c.Group(It.IsAny<string>()), Times.Never);
        _groupClient.Verify(c => c.UpdateConfig(It.IsAny<ConfigUpdatePayload>()), Times.Never);

        // Info-log captured: "secret will land on next bootstrap" — the
        // contractual signal to the operator that delivery is deferred.
        logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("no online runtime")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Theory]
    [InlineData(RuntimeState.Pending)]
    [InlineData(RuntimeState.Booting)]
    [InlineData(RuntimeState.Bootstrapping)]
    [InlineData(RuntimeState.Suspending)]
    [InlineData(RuntimeState.Suspended)]
    [InlineData(RuntimeState.Waking)]
    [InlineData(RuntimeState.Crashed)]
    [InlineData(RuntimeState.Failed)]
    [InlineData(RuntimeState.Deleting)]
    [InlineData(RuntimeState.Deleted)]
    public async Task Non_online_states_skip_fan_out(RuntimeState state)
    {
        var (encryption, _) = SecretsTestHarness.Build(_dbName);
        var projectId = Guid.NewGuid();

        await using (var seed = SecretsTestHarness.OpenDb(_dbName))
        {
            seed.ProjectRuntimes.Add(new ProjectRuntime
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                State = state,
                Region = "arn",
                CreatedAt = DateTime.UtcNow,
                StateChangedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        await using var ctx = SecretsTestHarness.OpenDb(_dbName);
        var handler = BuildHandler(ctx, encryption);

        await handler.Handle(
            new SecretsChanged(projectId, "ANY_KEY", Deleted: false, BranchId: null),
            CancellationToken.None);

        _groupClient.Verify(c => c.UpdateConfig(It.IsAny<ConfigUpdatePayload>()), Times.Never);
    }

    [Fact]
    public async Task Multiple_runtimes_picks_most_recent_by_created_at()
    {
        // Defensive: in normal operation there's one ProjectRuntime per project.
        // During a teardown-and-reprovision overlap two non-deleted rows may
        // briefly coexist; targeting follows RuntimeStatusController's
        // OrderByDescending(CreatedAt) tiebreak so the freshly-provisioned
        // runtime — the one a daemon would actually be connected to — wins.
        var (encryption, _) = SecretsTestHarness.Build(_dbName);
        var projectId = Guid.NewGuid();
        var olderRuntimeId = Guid.NewGuid();
        var newerRuntimeId = Guid.NewGuid();

        // The audit interceptor overwrites IAuditable.CreatedAt on insert
        // (ApplicationDbContext.SaveChangesAsync), so we save first, then
        // bypass the interceptor by mutating + saving via raw entity update
        // to pin distinct CreatedAt values for the OrderBy assertion.
        await using (var seed = SecretsTestHarness.OpenDb(_dbName))
        {
            seed.ProjectRuntimes.Add(new ProjectRuntime
            {
                Id = olderRuntimeId,
                ProjectId = projectId,
                State = RuntimeState.Online,
                Region = "arn",
                StateChangedAt = DateTime.UtcNow,
            });
            seed.ProjectRuntimes.Add(new ProjectRuntime
            {
                Id = newerRuntimeId,
                ProjectId = projectId,
                State = RuntimeState.Online,
                Region = "arn",
                StateChangedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();

            // The interceptor only runs on Added/Modified IAuditable entries
            // and only stamps UpdatedAt on Modified — CreatedAt is preserved.
            // So a second SaveChangesAsync that mutates CreatedAt directly
            // sticks. (This is why the production code never touches
            // CreatedAt — but tests need deterministic values.)
            var older = await seed.ProjectRuntimes.FindAsync(olderRuntimeId);
            var newer = await seed.ProjectRuntimes.FindAsync(newerRuntimeId);
            older!.CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            newer!.CreatedAt = new DateTime(2026, 5, 9, 0, 0, 0, DateTimeKind.Utc);
            await seed.SaveChangesAsync();
        }

        ConfigUpdatePayload? captured = null;
        _groupClient
            .Setup(c => c.UpdateConfig(It.IsAny<ConfigUpdatePayload>()))
            .Callback<ConfigUpdatePayload>(p => captured = p)
            .Returns(Task.CompletedTask);

        await using var ctx = SecretsTestHarness.OpenDb(_dbName);
        var handler = BuildHandler(ctx, encryption);

        await handler.Handle(
            new SecretsChanged(projectId, "MY_KEY", Deleted: true, BranchId: null),
            CancellationToken.None);

        _clients.Verify(c => c.Group($"runtime-{newerRuntimeId}"), Times.Once);
        _clients.Verify(c => c.Group($"runtime-{olderRuntimeId}"), Times.Never);
        captured.Should().NotBeNull();
        captured!.RuntimeId.Should().Be(newerRuntimeId);
    }

    // --- Auto-restart-on-fill ------------------------------------------------
    //
    // When a fill/update lands a value for an env var a service declared as
    // REQUIRED in the current expanded V2 spec, the handler restarts that
    // service so it picks up the value. The resolver is mocked to return the
    // V2 JSON; a real ProjectSecret row + the harness encryption service make
    // the UpdateConfig decrypt path succeed before the restart block runs.

    private const string SpecWithApiRequiringOpenRouter =
        """
        {
          "version": 2,
          "services": [
            {
              "name": "api",
              "command": "dotnet run",
              "requiredEnv": [ { "key": "OPENROUTER_API_KEY", "secret": true } ]
            }
          ]
        }
        """;

    [Fact]
    public async Task Fill_of_required_env_var_restarts_declaring_service()
    {
        var (encryption, _) = SecretsTestHarness.Build(_dbName);
        var projectId = Guid.NewGuid();
        var runtimeId = Guid.NewGuid();

        await using (var seed = SecretsTestHarness.OpenDb(_dbName))
        {
            seed.ProjectRuntimes.Add(new ProjectRuntime
            {
                Id = runtimeId,
                ProjectId = projectId,
                State = RuntimeState.Online,
                Region = "arn",
                CreatedAt = DateTime.UtcNow,
                StateChangedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        // Real ciphertext so the UpdateConfig decrypt path (which runs before
        // the restart block) succeeds against the harness encryption service.
        var (ciphertext, nonce, dekVersion) = await encryption.EncryptAsync(
            projectId, "sk_or_live_abc", CancellationToken.None);

        await using (var seed = SecretsTestHarness.OpenDb(_dbName))
        {
            seed.ProjectSecrets.Add(new ProjectSecret
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                Key = "OPENROUTER_API_KEY",
                Ciphertext = ciphertext,
                Nonce = nonce,
                DekVersion = dekVersion,
                Version = 1,
                CreatedBy = "user-1",
            });
            await seed.SaveChangesAsync();
        }

        // The current expanded spec declares OPENROUTER_API_KEY as required on "api".
        _resolver
            .Setup(r => r.ResolveAsync(projectId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SpecWithApiRequiringOpenRouter);

        _groupClient
            .Setup(c => c.UpdateConfig(It.IsAny<ConfigUpdatePayload>()))
            .Returns(Task.CompletedTask);
        _groupClient
            .Setup(c => c.RestartService(It.IsAny<RestartServicePayload>()))
            .Returns(Task.CompletedTask);

        await using var ctx = SecretsTestHarness.OpenDb(_dbName);
        var handler = BuildHandler(ctx, encryption);

        // Act
        await handler.Handle(
            new SecretsChanged(projectId, "OPENROUTER_API_KEY", Deleted: false, BranchId: null),
            CancellationToken.None);

        // Assert: the declaring service "api" is restarted exactly once, after
        // the env-delta push.
        _groupClient.Verify(c => c.UpdateConfig(It.IsAny<ConfigUpdatePayload>()), Times.Once);
        _groupClient.Verify(
            c => c.RestartService(It.Is<RestartServicePayload>(p =>
                p.ServiceName == "api"
                && p.RuntimeId == runtimeId
                && p.Reason.Contains("OPENROUTER_API_KEY"))),
            Times.Once);
    }

    [Fact]
    public async Task Fill_of_unrelated_env_var_does_not_restart_any_service()
    {
        var (encryption, _) = SecretsTestHarness.Build(_dbName);
        var projectId = Guid.NewGuid();
        var runtimeId = Guid.NewGuid();

        await using (var seed = SecretsTestHarness.OpenDb(_dbName))
        {
            seed.ProjectRuntimes.Add(new ProjectRuntime
            {
                Id = runtimeId,
                ProjectId = projectId,
                State = RuntimeState.Online,
                Region = "arn",
                CreatedAt = DateTime.UtcNow,
                StateChangedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var (ciphertext, nonce, dekVersion) = await encryption.EncryptAsync(
            projectId, "whatever", CancellationToken.None);

        await using (var seed = SecretsTestHarness.OpenDb(_dbName))
        {
            seed.ProjectSecrets.Add(new ProjectSecret
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                Key = "UNRELATED_KEY",
                Ciphertext = ciphertext,
                Nonce = nonce,
                DekVersion = dekVersion,
                Version = 1,
                CreatedBy = "user-1",
            });
            await seed.SaveChangesAsync();
        }

        // Same spec: "api" requires OPENROUTER_API_KEY, NOT UNRELATED_KEY.
        _resolver
            .Setup(r => r.ResolveAsync(projectId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SpecWithApiRequiringOpenRouter);

        _groupClient
            .Setup(c => c.UpdateConfig(It.IsAny<ConfigUpdatePayload>()))
            .Returns(Task.CompletedTask);

        await using var ctx = SecretsTestHarness.OpenDb(_dbName);
        var handler = BuildHandler(ctx, encryption);

        // Act
        await handler.Handle(
            new SecretsChanged(projectId, "UNRELATED_KEY", Deleted: false, BranchId: null),
            CancellationToken.None);

        // Assert: env-delta still pushed, but no service declared this key as
        // required → no restart.
        _groupClient.Verify(c => c.UpdateConfig(It.IsAny<ConfigUpdatePayload>()), Times.Once);
        _groupClient.Verify(c => c.RestartService(It.IsAny<RestartServicePayload>()), Times.Never);
    }
}

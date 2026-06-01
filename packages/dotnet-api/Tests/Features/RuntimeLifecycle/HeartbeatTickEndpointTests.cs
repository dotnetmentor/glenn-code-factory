using System.Security.Claims;
using Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Source.Features.RuntimeLifecycle.Controllers;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.RuntimeTokens.Models;

namespace Api.Tests.Features.RuntimeLifecycle;

/// <summary>
/// Unit tests for <see cref="RuntimeStatusController.HeartbeatTick"/> — the
/// daemon-side liveness ping fired from a worker_thread, independent of the
/// SignalR <c>Heartbeat</c> path on the main event loop (Fix #1 of the
/// 2026-05-24 runtime-unavailable investigation).
///
/// <para>Same harness pattern as <see cref="Api.Tests.Features.Conversations.GetActiveSessionForRuntimeTests"/>:
/// instantiate the controller directly against the in-memory DbContext, stamp
/// a pre-authenticated <see cref="ClaimsPrincipal"/> carrying (or not) the
/// <c>rt_runtime</c> claim, drive the action method directly. The auth pipeline
/// itself (JWT bearer middleware → 401 on missing/expired/revoked tokens)
/// is covered by RuntimeTokenServiceTests and the existing GetActiveSession
/// integration tests; this file owns the controller-level claim-vs-path check
/// and the DB write.</para>
/// </summary>
public class HeartbeatTickEndpointTests : HandlerTestBase
{
    private RuntimeStatusController CreateController(Guid? claimRuntimeId)
    {
        var identity = new ClaimsIdentity(authenticationType: "RuntimeToken");
        if (claimRuntimeId.HasValue)
        {
            identity.AddClaim(new Claim(RuntimeTokenClaimNames.RuntimeId, claimRuntimeId.Value.ToString()));
        }
        var principal = new ClaimsPrincipal(identity);

        var httpContext = new DefaultHttpContext { User = principal };

        return new RuntimeStatusController(Context)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private async Task<ProjectRuntime> SeedRuntimeAsync(
        DateTime? lastHeartbeatAt = null,
        bool isDeleted = false)
    {
        var runtime = new ProjectRuntime
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            BranchId = Guid.NewGuid(),
            Region = "arn",
            LastHeartbeatAt = lastHeartbeatAt,
            IsDeleted = isDeleted,
        };
        Context.ProjectRuntimes.Add(runtime);
        await Context.SaveChangesAsync();
        return runtime;
    }

    [Fact]
    public async Task ValidTokenForExistingRuntime_StampsLastHeartbeat_Returns204()
    {
        // Seed with a stale heartbeat 5 minutes ago; assert the endpoint
        // refreshes it to "approximately now".
        var staleAt = DateTime.UtcNow.AddMinutes(-5);
        var runtime = await SeedRuntimeAsync(lastHeartbeatAt: staleAt);

        var controller = CreateController(claimRuntimeId: runtime.Id);
        var before = DateTime.UtcNow;
        var result = await controller.HeartbeatTick(runtime.Id, CancellationToken.None);
        var after = DateTime.UtcNow;

        result.Should().BeOfType<NoContentResult>();

        // Re-load and check the timestamp landed inside the [before, after] window.
        var reloaded = await Context.ProjectRuntimes.FindAsync(runtime.Id);
        reloaded.Should().NotBeNull();
        reloaded!.LastHeartbeatAt.Should().NotBeNull();
        reloaded.LastHeartbeatAt!.Value.Should().BeOnOrAfter(before);
        reloaded.LastHeartbeatAt.Value.Should().BeOnOrBefore(after);
        reloaded.LastHeartbeatAt.Value.Should().NotBe(staleAt,
            "the endpoint must overwrite the stale timestamp — that's its entire purpose");
    }

    [Fact]
    public async Task RuntimeWithNullLastHeartbeat_StampsForFirstTime_Returns204()
    {
        // Fresh runtime that has never beat yet (LastHeartbeatAt = null). The
        // endpoint should still stamp it — covers the cold-boot path where the
        // worker_thread liveness ping wins the race against the main-thread
        // SignalR Heartbeat.
        var runtime = await SeedRuntimeAsync(lastHeartbeatAt: null);

        var controller = CreateController(claimRuntimeId: runtime.Id);
        var result = await controller.HeartbeatTick(runtime.Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();

        var reloaded = await Context.ProjectRuntimes.FindAsync(runtime.Id);
        reloaded!.LastHeartbeatAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ClaimMissing_Returns403_DoesNotStamp()
    {
        var runtime = await SeedRuntimeAsync(lastHeartbeatAt: null);

        var controller = CreateController(claimRuntimeId: null);
        var result = await controller.HeartbeatTick(runtime.Id, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();

        var reloaded = await Context.ProjectRuntimes.FindAsync(runtime.Id);
        reloaded!.LastHeartbeatAt.Should().BeNull(
            "a 403 must short-circuit before the DB write — otherwise an authenticated-but-malformed principal could keep-alive any runtime by path id alone");
    }

    [Fact]
    public async Task ClaimMismatchesPath_Returns403_DoesNotStamp()
    {
        // Stolen-token defence: token authenticated for runtime A is presented
        // against runtime B's URL. 403, no DB write. The auth scheme would
        // normally pin per-connection but for HTTP the bearer is portable —
        // this controller-level check is the gatekeeper.
        var runtimeA = await SeedRuntimeAsync(lastHeartbeatAt: null);
        var runtimeB = await SeedRuntimeAsync(lastHeartbeatAt: null);

        var controller = CreateController(claimRuntimeId: runtimeA.Id);
        var result = await controller.HeartbeatTick(runtimeB.Id, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();

        var reloadedB = await Context.ProjectRuntimes.FindAsync(runtimeB.Id);
        reloadedB!.LastHeartbeatAt.Should().BeNull(
            "runtime B must not have been kept alive by a token issued for runtime A");
    }

    [Fact]
    public async Task ClaimNotAGuid_Returns403()
    {
        // Authenticated principal but rt_runtime claim isn't a parseable Guid.
        // Defensive — the JWT issuer would reject this, but we guard anyway.
        var runtime = await SeedRuntimeAsync();

        var identity = new ClaimsIdentity(authenticationType: "RuntimeToken");
        identity.AddClaim(new Claim(RuntimeTokenClaimNames.RuntimeId, "not-a-guid"));
        var principal = new ClaimsPrincipal(identity);
        var httpContext = new DefaultHttpContext { User = principal };
        var controller = new RuntimeStatusController(Context)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };

        var result = await controller.HeartbeatTick(runtime.Id, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task RuntimeNotFound_Returns404()
    {
        // Token authenticated for a runtime id that does not exist (e.g. the
        // runtime row was deleted between token mint and this call). The
        // claim-vs-path check passes (both are the unknown id), but the DB
        // lookup returns null → 404.
        var unknownId = Guid.NewGuid();

        var controller = CreateController(claimRuntimeId: unknownId);
        var result = await controller.HeartbeatTick(unknownId, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task SoftDeletedRuntime_Returns404()
    {
        // The global query filter on ProjectRuntime hides IsDeleted=true rows
        // from default reads, so a janitor-marked runtime returns 404 here —
        // matching the SignalR Heartbeat handler's "ignore deleted" path.
        var runtime = await SeedRuntimeAsync(isDeleted: true);

        var controller = CreateController(claimRuntimeId: runtime.Id);
        var result = await controller.HeartbeatTick(runtime.Id, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>(
            "soft-deleted runtimes are invisible to the heartbeat endpoint; the daemon for one shouldn't be able to extend its life");
    }
}

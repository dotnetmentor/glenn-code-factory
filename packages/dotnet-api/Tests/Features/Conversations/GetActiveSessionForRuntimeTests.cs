using System.Security.Claims;
using Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Source.Features.Conversations.Models;
using Source.Features.RuntimeLifecycle.Controllers;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.RuntimeTokens.Models;
using Source.Infrastructure;

namespace Api.Tests.Features.Conversations;

/// <summary>
/// Unit tests for <see cref="RuntimeStatusController.GetActiveSession"/> — the
/// daemon-resume probe a freshly respawned runtime calls on boot. We instantiate
/// the controller directly against a real in-memory <see cref="ApplicationDbContext"/>
/// (via <see cref="HandlerTestBase"/>) and stamp a <see cref="DefaultHttpContext"/>
/// pre-populated with a <see cref="ClaimsPrincipal"/> carrying the
/// <c>rt_runtime</c> claim — i.e. we test what the controller does *after* the
/// JWT bearer middleware has authenticated the caller. No HTTP pipeline, no
/// SignalR.
///
/// <para><b>What this file does NOT cover:</b> the JWT bearer middleware itself
/// (missing token → 401, expired token → 401, revoked jti → 401 via
/// <c>JwtBearerEvents.OnTokenValidated</c>). That wiring lives in
/// <c>AuthenticationExtensions.AddRuntimeTokenAuthScheme</c> and is exercised
/// indirectly by <see cref="Source.Features.RuntimeTokens.Services.RuntimeTokenService"/>
/// validation tests (RuntimeTokenServiceTests) and the revocation cache tests
/// (RevocationCacheTests). A full integration harness for the auth pipeline
/// is deferred — the cumulative coverage of mint+validate (Card 3) and
/// revoke (Cards 4–5) plus this controller's claim-vs-path check is enough
/// for the spec's auth story.</para>
/// </summary>
public class GetActiveSessionForRuntimeTests : HandlerTestBase
{
    /// <summary>
    /// Build a controller whose <see cref="ControllerBase.User"/> carries the
    /// supplied claim runtime id. <paramref name="claimRuntimeId"/> = null
    /// simulates "authenticated but no rt_runtime claim" (claim missing).
    /// </summary>
    private RuntimeStatusController CreateController(Guid? claimRuntimeId)
    {
        var identity = new ClaimsIdentity(authenticationType: "RuntimeToken");
        if (claimRuntimeId.HasValue)
        {
            identity.AddClaim(new Claim(RuntimeTokenClaimNames.RuntimeId, claimRuntimeId.Value.ToString()));
        }
        var principal = new ClaimsPrincipal(identity);

        var httpContext = new DefaultHttpContext
        {
            User = principal,
        };

        return new RuntimeStatusController(Context)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext,
            },
        };
    }

    private async Task<ProjectRuntime> SeedRuntimeAsync(Guid projectId, Guid? branchId = null, bool isDeleted = false)
    {
        var runtime = new ProjectRuntime
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            BranchId = branchId ?? Guid.NewGuid(),
            Region = "arn",
            IsDeleted = isDeleted,
        };
        Context.ProjectRuntimes.Add(runtime);
        await Context.SaveChangesAsync();
        return runtime;
    }

    private async Task<Conversation> SeedConversationAsync(
        Guid projectId,
        Guid? branchId = null,
        ConversationStatus status = ConversationStatus.Active)
    {
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            BranchId = branchId ?? Guid.NewGuid(),
            Title = "test",
            Status = status,
            LastActivityAt = DateTime.UtcNow,
        };
        Context.Conversations.Add(conversation);
        await Context.SaveChangesAsync();
        return conversation;
    }

    private async Task<AgentSession> SeedSessionAsync(
        Guid conversationId,
        AgentSessionStatus status,
        DateTime? completedAt = null,
        DateTime? createdAt = null,
        string prompt = "resume me",
        string? claudeSessionId = null)
    {
        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            Prompt = prompt,
            Status = status,
            CompletedAt = completedAt,
            AgentId = claudeSessionId,
        };
        Context.AgentSessions.Add(session);
        await Context.SaveChangesAsync();

        // CreatedAt is auto-stamped by the audit interceptor on real saves;
        // the in-memory DB does the same via IAuditable plumbing. If the test
        // wants a specific ordering we overwrite after save and persist again.
        if (createdAt.HasValue)
        {
            session.CreatedAt = createdAt.Value;
            await Context.SaveChangesAsync();
        }
        return session;
    }

    // ----------------------------------------------------------------------

    [Fact]
    public async Task ActiveSessionReturned_WhenRunningSessionExists()
    {
        var projectId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var runtime = await SeedRuntimeAsync(projectId, branchId);
        var conversation = await SeedConversationAsync(projectId, branchId);
        var session = await SeedSessionAsync(
            conversation.Id,
            AgentSessionStatus.Running,
            completedAt: null,
            prompt: "do the thing",
            claudeSessionId: "claude-abc");

        var controller = CreateController(claimRuntimeId: runtime.Id);
        var result = await controller.GetActiveSession(runtime.Id, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<ActiveSessionResponse>().Subject;
        payload.SessionId.Should().Be(session.Id);
        payload.ConversationId.Should().Be(conversation.Id);
        payload.Prompt.Should().Be("do the thing");
        payload.AgentId.Should().Be("claude-abc");
    }

    [Fact]
    public async Task MostRecentRunningSessionWins()
    {
        var projectId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var runtime = await SeedRuntimeAsync(projectId, branchId);
        var conversation = await SeedConversationAsync(projectId, branchId);

        await SeedSessionAsync(
            conversation.Id, AgentSessionStatus.Running,
            createdAt: DateTime.UtcNow.AddMinutes(-10),
            prompt: "older");
        var newer = await SeedSessionAsync(
            conversation.Id, AgentSessionStatus.Running,
            createdAt: DateTime.UtcNow,
            prompt: "newer");

        var controller = CreateController(claimRuntimeId: runtime.Id);
        var result = await controller.GetActiveSession(runtime.Id, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<ActiveSessionResponse>().Subject;
        payload.SessionId.Should().Be(newer.Id);
        payload.Prompt.Should().Be("newer");
    }

    [Fact]
    public async Task NonRunningStatuses_Returns204()
    {
        var projectId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var runtime = await SeedRuntimeAsync(projectId, branchId);
        var conversation = await SeedConversationAsync(projectId, branchId);

        await SeedSessionAsync(conversation.Id, AgentSessionStatus.Succeeded, completedAt: DateTime.UtcNow);
        await SeedSessionAsync(conversation.Id, AgentSessionStatus.Failed,    completedAt: DateTime.UtcNow);
        await SeedSessionAsync(conversation.Id, AgentSessionStatus.Canceled,  completedAt: DateTime.UtcNow);

        var controller = CreateController(claimRuntimeId: runtime.Id);
        var result = await controller.GetActiveSession(runtime.Id, CancellationToken.None);

        result.Result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task DifferentProject_Returns204()
    {
        var thisProjectId  = Guid.NewGuid();
        var otherProjectId = Guid.NewGuid();
        var runtime = await SeedRuntimeAsync(thisProjectId);

        // Seed a Running session under a *different* project's conversation —
        // it must not leak into this runtime's resume probe.
        var otherConversation = await SeedConversationAsync(otherProjectId);
        await SeedSessionAsync(otherConversation.Id, AgentSessionStatus.Running);

        var controller = CreateController(claimRuntimeId: runtime.Id);
        var result = await controller.GetActiveSession(runtime.Id, CancellationToken.None);

        result.Result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task DifferentBranch_SameProject_Returns204()
    {
        // Same project, sibling branches. The conversation join in
        // GetActiveSession filters on both ProjectId AND BranchId — a session
        // belonging to a sibling branch's conversation must NOT be returned
        // for this runtime, even though the project matches. Defense-in-depth
        // against silent cross-branch session handoff.
        var projectId = Guid.NewGuid();
        var thisBranchId  = Guid.NewGuid();
        var otherBranchId = Guid.NewGuid();
        var runtime = await SeedRuntimeAsync(projectId, thisBranchId);

        var siblingConversation = await SeedConversationAsync(projectId, otherBranchId);
        await SeedSessionAsync(siblingConversation.Id, AgentSessionStatus.Running);

        var controller = CreateController(claimRuntimeId: runtime.Id);
        var result = await controller.GetActiveSession(runtime.Id, CancellationToken.None);

        result.Result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task RuntimeNotFound_Returns404()
    {
        var unknownRuntimeId = Guid.NewGuid();

        // Claim matches the path id, but the runtime row doesn't exist.
        var controller = CreateController(claimRuntimeId: unknownRuntimeId);
        var result = await controller.GetActiveSession(unknownRuntimeId, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task SoftDeletedRuntime_Returns404()
    {
        var projectId = Guid.NewGuid();
        var runtime = await SeedRuntimeAsync(projectId, branchId: null, isDeleted: true);

        var controller = CreateController(claimRuntimeId: runtime.Id);
        var result = await controller.GetActiveSession(runtime.Id, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>(
            "global query filter on ProjectRuntime hides IsDeleted=true rows; a deleted runtime has no resumable session");
    }

    [Fact]
    public async Task ClaimMissing_Returns403()
    {
        // Authenticated principal but with NO rt_runtime claim. The auth scheme would have
        // already rejected this case in production (a valid RuntimeToken always carries
        // rt_runtime), but the controller still defends against malformed principals.
        // Pre-rewrite this test asserted 401 from a missing X-Runtime-Id header; now that
        // 401 lives in the JWT bearer middleware (no token → middleware-level 401), and
        // controller-level "valid principal but missing claim" maps to 403.
        var projectId = Guid.NewGuid();
        var runtime = await SeedRuntimeAsync(projectId);

        var controller = CreateController(claimRuntimeId: null);
        var result = await controller.GetActiveSession(runtime.Id, CancellationToken.None);

        result.Result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task ClaimMismatchesPath_Returns403()
    {
        var projectId = Guid.NewGuid();
        var runtime = await SeedRuntimeAsync(projectId);

        // Claim carries a different (still-valid) Guid — a daemon may only ask
        // about itself. 403, not 401: the caller IS authenticated, just unauthorised
        // for this resource.
        var controller = CreateController(claimRuntimeId: Guid.NewGuid());
        var result = await controller.GetActiveSession(runtime.Id, CancellationToken.None);

        result.Result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task ValidRuntimeButNoRunningSession_Returns204()
    {
        var projectId = Guid.NewGuid();
        var runtime = await SeedRuntimeAsync(projectId);
        // No conversation, no session — runtime exists, claim matches, but
        // there's nothing to resume.

        var controller = CreateController(claimRuntimeId: runtime.Id);
        var result = await controller.GetActiveSession(runtime.Id, CancellationToken.None);

        result.Result.Should().BeOfType<NoContentResult>();
    }
}

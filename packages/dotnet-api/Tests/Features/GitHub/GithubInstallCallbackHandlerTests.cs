using Api.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Moq;
using Source.Features.GitHub.Commands;
using Source.Features.GitHub.Configuration;
using Source.Features.GitHub.Models;
using Source.Features.GitHub.Services;
using Source.Features.GitHub.Services.Dtos;
using Source.Features.Users.Models;
using Source.Features.Workspaces.Models;

namespace Api.Tests.Features.GitHub;

/// <summary>
/// Handler-level tests for the GitHub install callback. Focused on the state-token sign/verify
/// cycle and the cross-workspace guard. The HTTP layer (GithubApiClient + GithubAppTokenService)
/// is fully mocked.
/// </summary>
public class GithubInstallCallbackHandlerTests : HandlerTestBase
{
    private const string Secret = "test-secret-for-state-handler-tests";

    private readonly GithubInstallStateService _state;
    private readonly Mock<IGithubApiClient> _api = new(MockBehavior.Loose);
    private readonly Mock<IGithubAppTokenService> _tokens = new(MockBehavior.Loose);

    public GithubInstallCallbackHandlerTests()
    {
        var options = new StubGithubOptionsAccessor(new GithubOptions { WebhookSecret = Secret });
        _state = new GithubInstallStateService(options);

        _tokens.Setup(t => t.GetInstallationTokenAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghs_fake");
    }

    // -----------------------------------------------------------------------
    // State token sign + verify cycle (the unit slice)
    // -----------------------------------------------------------------------

    [Fact]
    public void State_Issue_then_Verify_returns_workspace_id()
    {
        var workspaceId = Guid.NewGuid();
        var token = _state.Issue(workspaceId, TimeSpan.FromMinutes(5));

        var verified = _state.Verify(token);

        verified.Should().Be(workspaceId);
    }

    [Fact]
    public void State_Verify_rejects_tampered_payload()
    {
        var workspaceId = Guid.NewGuid();
        var token = _state.Issue(workspaceId, TimeSpan.FromMinutes(5));

        // Flip a single character in the payload portion.
        var dot = token.IndexOf('.');
        var firstPayloadChar = token[0] == 'a' ? 'b' : 'a';
        var tampered = firstPayloadChar + token[1..dot] + token[dot..];

        _state.Verify(tampered).Should().BeNull();
    }

    [Fact]
    public void State_Verify_rejects_tampered_signature()
    {
        var token = _state.Issue(Guid.NewGuid(), TimeSpan.FromMinutes(5));

        var dot = token.IndexOf('.');
        var sigStart = dot + 1;
        var lastSigChar = token[^1] == 'A' ? 'B' : 'A';
        var tampered = token[..sigStart] + token[sigStart..^1] + lastSigChar;

        _state.Verify(tampered).Should().BeNull();
    }

    [Fact]
    public void State_Verify_rejects_expired_token()
    {
        var token = _state.Issue(Guid.NewGuid(), TimeSpan.FromSeconds(-1));

        _state.Verify(token).Should().BeNull();
    }

    [Fact]
    public void State_Verify_rejects_token_signed_with_different_secret()
    {
        var alt = new GithubInstallStateService(new StubGithubOptionsAccessor(new GithubOptions { WebhookSecret = "other-secret" }));
        var foreign = alt.Issue(Guid.NewGuid(), TimeSpan.FromMinutes(5));

        _state.Verify(foreign).Should().BeNull();
    }

    [Fact]
    public void State_Verify_rejects_garbage()
    {
        _state.Verify(null).Should().BeNull();
        _state.Verify("").Should().BeNull();
        _state.Verify("not-a-token").Should().BeNull();
        _state.Verify("aaa.bbb").Should().BeNull();
        _state.Verify(".bbb").Should().BeNull();
        _state.Verify("aaa.").Should().BeNull();
    }

    // -----------------------------------------------------------------------
    // Handler — cross-workspace guard, idempotency, missing-installation_id
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Handler_400_when_state_token_is_missing()
    {
        var handler = BuildHandler(out var sync);
        var result = await handler.Handle(
            new HandleGithubInstallCallbackCommand(StateToken: null, StateCookieValue: null, InstallationId: 1, SetupAction: null),
            CancellationToken.None);
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("Invalid state");
        sync.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Handler_400_when_cookie_and_query_state_differ()
    {
        var workspaceId = await SeedWorkspaceAsync();
        var goodToken = _state.Issue(workspaceId, TimeSpan.FromMinutes(5));
        var otherToken = _state.Issue(workspaceId, TimeSpan.FromMinutes(5));

        var handler = BuildHandler(out _);
        var result = await handler.Handle(
            new HandleGithubInstallCallbackCommand(
                StateToken: goodToken,
                StateCookieValue: otherToken,
                InstallationId: 5,
                SetupAction: null),
            CancellationToken.None);
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("Invalid state");
    }

    [Fact]
    public async Task Handler_400_when_installation_id_missing()
    {
        var workspaceId = await SeedWorkspaceAsync();
        var token = _state.Issue(workspaceId, TimeSpan.FromMinutes(5));

        var handler = BuildHandler(out _);
        var result = await handler.Handle(
            new HandleGithubInstallCallbackCommand(token, token, InstallationId: null, SetupAction: null),
            CancellationToken.None);
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("installation_id");
    }

    [Fact]
    public async Task Handler_creates_installation_and_calls_sync_on_first_install()
    {
        var workspaceId = await SeedWorkspaceAsync();
        var token = _state.Issue(workspaceId, TimeSpan.FromMinutes(5));

        _api.Setup(a => a.GetInstallationAsync(42L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GithubInstallationDto(
                Id: 42L,
                Account: new GithubAccountDto(Id: 1, Login: "neo", Type: "User", AvatarUrl: null)));

        var handler = BuildHandler(out var sync);

        var result = await handler.Handle(
            new HandleGithubInstallCallbackCommand(token, token, 42L, "install"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Pending.Should().BeFalse();

        Context.GithubInstallations.Should().ContainSingle().Which.InstallationId.Should().Be(42L);
        sync.Verify(s => s.SyncAsync(It.IsAny<Guid>(), 42L, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handler_idempotent_on_re_callback_for_same_workspace()
    {
        var workspaceId = await SeedWorkspaceAsync();
        var token = _state.Issue(workspaceId, TimeSpan.FromMinutes(5));

        // Pre-seed an installation row so the handler hits the "existing" branch.
        Context.GithubInstallations.Add(new GithubInstallation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            InstallationId = 42L,
            AccountLogin = "neo",
            AccountType = "User",
        });
        await Context.SaveChangesAsync();

        var handler = BuildHandler(out var sync);

        var result = await handler.Handle(
            new HandleGithubInstallCallbackCommand(token, token, 42L, "install"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        Context.GithubInstallations.Count(i => i.InstallationId == 42L).Should().Be(1, "no duplicate row");
        _api.Verify(a => a.GetInstallationAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never,
            "metadata fetch is skipped on re-callback");
        sync.Verify(s => s.SyncAsync(It.IsAny<Guid>(), 42L, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handler_400_when_installation_already_belongs_to_another_workspace()
    {
        var workspaceA = await SeedWorkspaceAsync(slug: "a-ws");
        var workspaceB = await SeedWorkspaceAsync(slug: "b-ws");

        Context.GithubInstallations.Add(new GithubInstallation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceA,
            InstallationId = 99L,
            AccountLogin = "shared",
            AccountType = "Organization",
        });
        await Context.SaveChangesAsync();

        var token = _state.Issue(workspaceB, TimeSpan.FromMinutes(5));

        var handler = BuildHandler(out _);

        var result = await handler.Handle(
            new HandleGithubInstallCallbackCommand(token, token, 99L, "install"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("different workspace");
    }

    [Fact]
    public async Task Handler_pending_when_setup_action_is_request_and_no_existing_row()
    {
        var workspaceId = await SeedWorkspaceAsync();
        var token = _state.Issue(workspaceId, TimeSpan.FromMinutes(5));

        var handler = BuildHandler(out var sync);

        var result = await handler.Handle(
            new HandleGithubInstallCallbackCommand(token, token, 42L, "request"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Pending.Should().BeTrue();
        result.Value.GithubInstallationId.Should().Be(Guid.Empty);
        sync.VerifyNoOtherCalls();
    }

    // -----------------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------------

    private HandleGithubInstallCallbackHandler BuildHandler(out Mock<IGithubRepositorySyncService> sync)
    {
        sync = new Mock<IGithubRepositorySyncService>(MockBehavior.Loose);
        sync.Setup(s => s.SyncAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepositorySyncResult(0, 0, 0, 0));

        return new HandleGithubInstallCallbackHandler(
            Context,
            _state,
            _api.Object,
            sync.Object,
            new Mock<ILogger<HandleGithubInstallCallbackHandler>>().Object);
    }

    private async Task<Guid> SeedWorkspaceAsync(string slug = "test-ws")
    {
        var ownerId = $"owner-{Guid.NewGuid():N}";
        var owner = new User { Id = ownerId, UserName = $"{slug}@x.com", Email = $"{slug}@x.com" };
        Context.Users.Add(owner);

        var ws = new Workspace
        {
            Id = Guid.NewGuid(),
            Slug = slug + "-" + Guid.NewGuid().ToString("N")[..4],
            Name = slug,
            OwnerId = ownerId,
        };
        Context.Workspaces.Add(ws);
        await Context.SaveChangesAsync();
        return ws.Id;
    }
}

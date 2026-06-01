using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Source.Features.SignalR.Hubs;
using Source.Infrastructure;

namespace Api.Tests.Features.SignalR;

/// <summary>
/// Unit tests for <see cref="AgentHub"/>. We instantiate the hub directly,
/// inject a mocked <see cref="HubCallerContext"/> + <see cref="IGroupManager"/>
/// + <see cref="IHubCallerClients"/>, and verify the lifecycle hooks call the
/// expected SignalR primitives. Standard ASP.NET Core pattern — the framework
/// constructs hubs via DI and then assigns the three properties from the
/// invocation context, so we mirror that here.
/// </summary>
public class AgentHubTests
{
    [Fact]
    public async Task OnConnectedAsync_authenticated_user_joins_user_group()
    {
        // Arrange
        const string userId = "user-123";
        const string connectionId = "conn-abc";

        var context = new Mock<HubCallerContext>();
        context.SetupGet(c => c.ConnectionId).Returns(connectionId);
        context.SetupGet(c => c.User).Returns(BuildUserPrincipal(userId));

        var groups = new Mock<IGroupManager>();
        var clients = new Mock<IHubCallerClients<IAgentClient>>();

        var hub = new AgentHub(BuildDbContext(), Mock.Of<IHubContext<RuntimeHub, IRuntimeClient>>(), Mock.Of<Source.Features.Conversations.Services.ITurnDispatcher>(), Mock.Of<MediatR.IMediator>(), Mock.Of<Source.Features.SignalR.Services.IAgentSecretsResolver>(), NullLogger<AgentHub>.Instance)
        {
            Context = context.Object,
            Groups = groups.Object,
            Clients = clients.Object,
        };

        // Act
        await hub.OnConnectedAsync();

        // Assert — the per-user group is the only group joined in this card.
        groups.Verify(
            g => g.AddToGroupAsync(connectionId, $"user-{userId}", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task OnConnectedAsync_missing_claim_aborts()
    {
        var context = new Mock<HubCallerContext>();
        context.SetupGet(c => c.ConnectionId).Returns("conn-abc");
        context.SetupGet(c => c.User).Returns(new ClaimsPrincipal(new ClaimsIdentity()));

        var groups = new Mock<IGroupManager>();
        var clients = new Mock<IHubCallerClients<IAgentClient>>();

        var hub = new AgentHub(BuildDbContext(), Mock.Of<IHubContext<RuntimeHub, IRuntimeClient>>(), Mock.Of<Source.Features.Conversations.Services.ITurnDispatcher>(), Mock.Of<MediatR.IMediator>(), Mock.Of<Source.Features.SignalR.Services.IAgentSecretsResolver>(), NullLogger<AgentHub>.Instance)
        {
            Context = context.Object,
            Groups = groups.Object,
            Clients = clients.Object,
        };

        await hub.OnConnectedAsync();

        // Belt-and-braces: [Authorize] should have already rejected, but if a
        // user-less ClaimsPrincipal slips through we must abort and never join
        // a group with an empty / null user id.
        context.Verify(c => c.Abort(), Times.Once);
        groups.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static ApplicationDbContext BuildDbContext()
    {
        // Lifecycle tests don't touch the DB; a stub in-memory context is enough
        // to satisfy the AgentHub constructor without affecting behaviour.
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static ClaimsPrincipal BuildUserPrincipal(string userId)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId),
        }, authenticationType: "Test");
        return new ClaimsPrincipal(identity);
    }
}

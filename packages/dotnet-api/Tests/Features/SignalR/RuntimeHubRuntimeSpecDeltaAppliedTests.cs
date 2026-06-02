using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Source.Features.Conversations.Services;
using Source.Features.Health;
using Source.Features.Health.Services;
using Source.Features.RuntimeCuration.Commands;
using Source.Features.RuntimeCuration.Models;
using Source.Features.RuntimeTokens.Models;
using Source.Features.SignalR.Hubs;
using Source.Infrastructure;
using Source.Shared.Results;

namespace Api.Tests.Features.SignalR;

/// <summary>
/// Coverage for <see cref="RuntimeHub.RuntimeSpecDeltaApplied"/> — the daemon's
/// ack channel for an applied (or failed) spec delta. The hub method only does
/// claim extraction + MediatR dispatch; the row mutation lives in
/// <see cref="RecordApplyResultCommandHandler"/> and is tested separately.
/// </summary>
public class RuntimeHubRuntimeSpecDeltaAppliedTests : IDisposable
{
    private readonly ApplicationDbContext _db;
    private readonly Mock<IMediator> _mediator = new();
    private readonly Mock<IHubContext<AgentHub, IAgentClient>> _agentHub = new();

    public RuntimeHubRuntimeSpecDeltaAppliedTests()
    {
        _db = Api.Tests.Infrastructure.TestDbContextFactory.Create();
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private RuntimeHub CreateHub(Guid? runtimeId, Guid? projectId)
    {
        var identity = new ClaimsIdentity(authenticationType: "RuntimeToken");
        if (runtimeId.HasValue)
        {
            identity.AddClaim(new Claim(RuntimeTokenClaimNames.RuntimeId, runtimeId.Value.ToString()));
        }
        if (projectId.HasValue)
        {
            identity.AddClaim(new Claim(RuntimeTokenClaimNames.ProjectId, projectId.Value.ToString()));
        }
        var principal = new ClaimsPrincipal(identity);

        var http = new DefaultHttpContext { User = principal };
        var context = new FakeHubCallerContextWithUser("conn-1", http, principal);

        var hub = new RuntimeHub(
            _db,
            _mediator.Object,
            _agentHub.Object,
            Mock.Of<ITurnDispatcher>(),
            new HealthSnapshotBuffer(),
            new ServiceDownDetector(),
            // SecretEncryptionService + IGithubAppTokenService + IAgentPermissionsResolver
            // + ISystemSettingsService + IAgentSecretsResolver unused on this path — null! placeholders.
            null!,
            null!,
            null!,
            null!,
            null!,
            new Api.Tests.Infrastructure.FakeClock(),
            NullLogger<RuntimeHub>.Instance)
        {
            Context = context,
            Groups = Mock.Of<IGroupManager>(),
            Clients = Mock.Of<IHubCallerClients<IRuntimeClient>>(),
        };
        return hub;
    }

    // ----------------------------------------------------------------------

    [Fact]
    public async Task ValidClaims_DispatchesRecordApplyResultCommand()
    {
        var runtimeId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var proposalId = Guid.NewGuid();

        _mediator
            .Setup(m => m.Send(It.IsAny<RecordApplyResultCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(Unit.Value));

        var hub = CreateHub(runtimeId, projectId);
        var payload = new RuntimeSpecDeltaApplyResultPayload(proposalId, Success: true, Error: null);
        await hub.RuntimeSpecDeltaApplied(payload);

        _mediator.Verify(m => m.Send(
            It.Is<RecordApplyResultCommand>(c =>
                c.RuntimeId == runtimeId &&
                c.ProjectId == projectId &&
                c.Payload.ProposalId == proposalId &&
                c.Payload.Success),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MissingRuntimeIdClaim_IsSilentNoOp()
    {
        var hub = CreateHub(runtimeId: null, projectId: Guid.NewGuid());
        var payload = new RuntimeSpecDeltaApplyResultPayload(
            ProposalId: Guid.NewGuid(), Success: true, Error: null);

        await hub.RuntimeSpecDeltaApplied(payload);

        _mediator.Verify(m => m.Send(
            It.IsAny<RecordApplyResultCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task MissingProjectIdClaim_IsSilentNoOp()
    {
        var hub = CreateHub(runtimeId: Guid.NewGuid(), projectId: null);
        var payload = new RuntimeSpecDeltaApplyResultPayload(
            ProposalId: Guid.NewGuid(), Success: false, Error: "boom");

        await hub.RuntimeSpecDeltaApplied(payload);

        _mediator.Verify(m => m.Send(
            It.IsAny<RecordApplyResultCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Hub caller-context stand-in that exposes <see cref="ClaimsPrincipal"/>
    /// in addition to ConnectionId / Items. The other RuntimeHub test fakes
    /// only need Items because their methods read from <c>Context.Items</c>;
    /// <c>RuntimeSpecDeltaApplied</c> reads claims off <c>Context.User</c>
    /// directly.
    /// </summary>
    private sealed class FakeHubCallerContextWithUser : HubCallerContext
    {
        private readonly string _connectionId;
        private readonly IFeatureCollection _features;
        private readonly Dictionary<object, object?> _items = new();
        private readonly ClaimsPrincipal _user;

        public FakeHubCallerContextWithUser(string connectionId, HttpContext httpContext, ClaimsPrincipal user)
        {
            _connectionId = connectionId;
            _features = new FeatureCollection();
            _features.Set<IHttpContextFeature>(new HttpContextFeature { HttpContext = httpContext });
            _user = user;
        }

        public override string ConnectionId => _connectionId;
        public override string? UserIdentifier => _user.FindFirstValue(ClaimTypes.NameIdentifier);
        public override ClaimsPrincipal? User => _user;
        public override IDictionary<object, object?> Items => _items;
        public override IFeatureCollection Features => _features;
        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public override void Abort() { }

        private sealed class HttpContextFeature : IHttpContextFeature
        {
            public HttpContext? HttpContext { get; set; }
        }
    }
}

using System.Reflection;
using Api.Tests.Infrastructure;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Source.Features.RuntimeTokens.Commands;
using Source.Features.RuntimeTokens.Controllers;
using Source.Features.RuntimeTokens.Models;
using Source.Features.RuntimeTokens.Services;
using Source.Features.SystemSettings.Services;
using Source.Infrastructure;
using Source.Infrastructure.AuthorizationModels;

namespace Api.Tests.Features.RuntimeTokens;

/// <summary>
/// Unit-level coverage for <see cref="RuntimeTokensAdminController"/>. Each test
/// drives the controller directly with a thin <see cref="StubMediator"/> wired
/// to real command handlers — the same handlers MediatR would resolve at runtime.
/// That keeps the cache-prime + idempotency policy under test without needing the
/// full ASP.NET pipeline. The auth gate itself (the [Authorize] attribute) is
/// asserted via reflection — pipeline middleware enforces it, so direct invocation
/// can't observe it; the unit assertion here mirrors how
/// <see cref="RuntimeAdminController"/> integration tests cover the same gate.
/// </summary>
public class RuntimeTokensAdminControllerTests : IDisposable
{
    private readonly string _dbName;
    private readonly ApplicationDbContext _ctx;

    public RuntimeTokensAdminControllerTests()
    {
        _dbName = Guid.NewGuid().ToString();
        _ctx = TestDbContextFactory.Create(_dbName);
    }

    public void Dispose()
    {
        _ctx.Dispose();
        GC.SuppressFinalize(this);
    }

    private ApplicationDbContext OpenDb() => TestDbContextFactory.Create(_dbName);

    private static RuntimeTokenIssue NewIssue(
        Guid? id = null,
        Guid? runtimeId = null,
        Guid? tenantId = null,
        DateTime? expiresAt = null,
        DateTime? revokedAt = null,
        string? revocationReason = null)
    {
        return new RuntimeTokenIssue
        {
            Id = id ?? Guid.NewGuid(),
            RuntimeId = runtimeId ?? Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            TenantId = tenantId,
            BranchId = null,
            Scope = "runtime",
            TokenHash = new string('a', 64),
            IssuedAt = DateTime.UtcNow.AddMinutes(-5),
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddHours(1),
            RevokedAt = revokedAt,
            RevocationReason = revocationReason,
        };
    }

    /// <summary>
    /// Build a controller wired against a freshly-created DbContext, real handlers
    /// for the three commands, and the supplied <paramref name="cache"/>. Each
    /// handler call gets its own DbContext so the shape mirrors how MediatR + DI
    /// would scope per-request.
    /// </summary>
    private RuntimeTokensAdminController BuildController(IRevocationCache cache)
    {
        var mediator = new StubMediator(this, cache);
        // The DbContext on the controller is only used by the new admin
        // List endpoint; the existing revoke tests don't exercise it, so a
        // shared scoped context is enough here.
        return new RuntimeTokensAdminController(mediator, _ctx);
    }

    // ----------------------------------------------------------------------
    // RevokeToken
    // ----------------------------------------------------------------------

    [Fact]
    public async Task RevokeToken_HappyPath_Returns200WithJti_AndPrimesCache()
    {
        // Build a real-token round-trip stack so the assertion can witness both
        // the DB row flipping AND the cache being primed by the handler.
        var cipherKeyB64 = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new SystemSettingsCipherOptions { EncryptionKey = cipherKeyB64 }));
        services.AddSingleton<ISystemSettingsCipher, SystemSettingsCipher>();
        services.AddSingleton<SystemSettingsCache>();
        services.AddScoped(_ => TestDbContextFactory.Create(_dbName));
        services.AddScoped<ISystemSettingsService, SystemSettingsService>();
        using var sp = services.BuildServiceProvider();

        var signingKeys = new RuntimeTokenSigningKeyService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RuntimeTokenSigningKeyService>.Instance);

        var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = new RevocationCache(
            memory,
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RevocationCache>.Instance);

        using var serviceDb = OpenDb();
        var tokenService = new RuntimeTokenService(
            signingKeys, serviceDb, cache, NullLogger<RuntimeTokenService>.Instance);

        var mintResult = await tokenService.MintAsync(new MintTokenRequest(
            RuntimeId: Guid.NewGuid(),
            ProjectId: Guid.NewGuid(),
            BranchId: null,
            TenantId: Guid.NewGuid(),
            Lifetime: TimeSpan.FromHours(1)));
        mintResult.IsSuccess.Should().BeTrue($"setup mint must succeed (error={mintResult.Error})");
        var minted = mintResult.Value;

        var controller = BuildController(cache);

        var action = await controller.RevokeToken(
            minted.Jti, new RevokeRequest("leaked"), CancellationToken.None);

        var ok = action.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.StatusCode.Should().Be(200);
        var payload = ok.Value.Should().BeOfType<RevokeTokenResponse>().Subject;
        payload.Jti.Should().Be(minted.Jti);

        cache.IsRevoked(minted.Jti).Should().BeTrue("the handler must prime the cache after a successful revoke");
    }

    [Fact]
    public async Task RevokeToken_EmptyReason_Returns400()
    {
        var issue = NewIssue();
        _ctx.RuntimeTokenIssues.Add(issue);
        await _ctx.SaveChangesAsync();

        var controller = BuildController(NoOpCache());

        var action = await controller.RevokeToken(
            issue.Id, new RevokeRequest("   "), CancellationToken.None);

        var bad = action.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task RevokeToken_UnknownJti_Returns404()
    {
        var controller = BuildController(NoOpCache());

        var action = await controller.RevokeToken(
            Guid.NewGuid(), new RevokeRequest("leaked"), CancellationToken.None);

        var notFound = action.Result.Should().BeOfType<NotFoundObjectResult>().Subject;
        notFound.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task RevokeToken_AlreadyRevoked_Returns200_Idempotent()
    {
        // Pre-revoke a row so the handler hits the idempotent success branch
        // (RevokedAt != null -> Result.Success without re-writing).
        var firstRevokedAt = DateTime.UtcNow.AddMinutes(-1);
        var issue = NewIssue(revokedAt: firstRevokedAt, revocationReason: "first");
        _ctx.RuntimeTokenIssues.Add(issue);
        await _ctx.SaveChangesAsync();

        var controller = BuildController(NoOpCache());

        var action = await controller.RevokeToken(
            issue.Id, new RevokeRequest("second"), CancellationToken.None);

        var ok = action.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.StatusCode.Should().Be(200);
        var payload = ok.Value.Should().BeOfType<RevokeTokenResponse>().Subject;
        payload.Jti.Should().Be(issue.Id);

        // First-revocation-wins: the original audit row is preserved.
        await using var verifyDb = OpenDb();
        var row = await verifyDb.RuntimeTokenIssues.SingleAsync(r => r.Id == issue.Id);
        row.RevocationReason.Should().Be("first");
    }

    // ----------------------------------------------------------------------
    // RevokeForRuntime
    // ----------------------------------------------------------------------

    [Fact]
    public async Task RevokeForRuntime_HappyPath_Returns200WithRevokedCount()
    {
        var runtimeId = Guid.NewGuid();
        _ctx.RuntimeTokenIssues.AddRange(
            NewIssue(runtimeId: runtimeId),
            NewIssue(runtimeId: runtimeId),
            NewIssue(runtimeId: runtimeId));
        await _ctx.SaveChangesAsync();

        var controller = BuildController(NoOpCache());

        var action = await controller.RevokeForRuntime(
            runtimeId, new RevokeRequest("rotation"), CancellationToken.None);

        var ok = action.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.StatusCode.Should().Be(200);
        var payload = ok.Value.Should().BeOfType<BulkRevokeResponse>().Subject;
        payload.RevokedCount.Should().Be(3);
    }

    [Fact]
    public async Task RevokeForRuntime_NoMatchingTokens_Returns200WithCountZero()
    {
        var controller = BuildController(NoOpCache());

        var action = await controller.RevokeForRuntime(
            Guid.NewGuid(), new RevokeRequest("rotation"), CancellationToken.None);

        var ok = action.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<BulkRevokeResponse>().Subject;
        payload.RevokedCount.Should().Be(0);
    }

    [Fact]
    public async Task RevokeForRuntime_LeavesOtherRuntimesAlone()
    {
        var runtimeA = Guid.NewGuid();
        var runtimeB = Guid.NewGuid();
        var aIssue = NewIssue(runtimeId: runtimeA);
        var bIssue = NewIssue(runtimeId: runtimeB);
        _ctx.RuntimeTokenIssues.AddRange(aIssue, bIssue);
        await _ctx.SaveChangesAsync();

        var controller = BuildController(NoOpCache());

        var action = await controller.RevokeForRuntime(
            runtimeA, new RevokeRequest("rotation"), CancellationToken.None);

        var ok = action.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<BulkRevokeResponse>().Subject;
        payload.RevokedCount.Should().Be(1);

        await using var verifyDb = OpenDb();
        var bRow = await verifyDb.RuntimeTokenIssues.SingleAsync(r => r.Id == bIssue.Id);
        bRow.RevokedAt.Should().BeNull("runtime B's tokens must be left intact");
    }

    [Fact]
    public async Task RevokeForRuntime_EmptyReason_Returns400()
    {
        var controller = BuildController(NoOpCache());

        var action = await controller.RevokeForRuntime(
            Guid.NewGuid(), new RevokeRequest("   "), CancellationToken.None);

        action.Result.Should().BeOfType<BadRequestObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    // ----------------------------------------------------------------------
    // RevokeForTenant
    // ----------------------------------------------------------------------

    [Fact]
    public async Task RevokeForTenant_HappyPath_LeavesOtherTenantUntouched()
    {
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();

        var t1Issue1 = NewIssue(tenantId: t1);
        var t1Issue2 = NewIssue(tenantId: t1);
        var t2Issue  = NewIssue(tenantId: t2);

        _ctx.RuntimeTokenIssues.AddRange(t1Issue1, t1Issue2, t2Issue);
        await _ctx.SaveChangesAsync();

        var controller = BuildController(NoOpCache());

        var action = await controller.RevokeForTenant(
            t1, new RevokeRequest("tenant-shutdown"), CancellationToken.None);

        var ok = action.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.StatusCode.Should().Be(200);
        var payload = ok.Value.Should().BeOfType<BulkRevokeResponse>().Subject;
        payload.RevokedCount.Should().Be(2);

        await using var verifyDb = OpenDb();
        var t2Row = await verifyDb.RuntimeTokenIssues.SingleAsync(r => r.Id == t2Issue.Id);
        t2Row.RevokedAt.Should().BeNull("tenant T2's tokens must be left intact");

        var t1Rows = await verifyDb.RuntimeTokenIssues
            .Where(r => r.TenantId == t1)
            .ToListAsync();
        t1Rows.Should().OnlyContain(r => r.RevokedAt != null);
    }

    [Fact]
    public async Task RevokeForTenant_EmptyReason_Returns400()
    {
        var controller = BuildController(NoOpCache());

        var action = await controller.RevokeForTenant(
            Guid.NewGuid(), new RevokeRequest(""), CancellationToken.None);

        action.Result.Should().BeOfType<BadRequestObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    // ----------------------------------------------------------------------
    // Authorization (reflection — pipeline-enforced, can't observe via direct
    // controller invocation; one assertion at the class level covers all three
    // endpoints since the attribute is declared on the controller class).
    // ----------------------------------------------------------------------

    [Fact]
    public void Controller_IsGatedBy_AuthorizeSuperAdminRole()
    {
        var attr = typeof(RuntimeTokensAdminController)
            .GetCustomAttribute<AuthorizeAttribute>();

        attr.Should().NotBeNull("operator-only surfaces must carry [Authorize]");
        attr!.Roles.Should().Be(RoleConstants.SuperAdmin,
            "the role gate must match every other admin surface (FlyAdmin, RuntimeAdmin, BootstrapRuns)");
    }

    // ----------------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------------

    private static IRevocationCache NoOpCache()
    {
        // The handler under test calls cache.Revoke(jti, exp); a no-op cache is
        // fine for the assertions that don't observe revocation state. We use a
        // real <see cref="RevocationCache"/> here so the InertScopeFactory path
        // isn't hit (it's only hit on IsRevoked when the cache is cold).
        var memory = new MemoryCache(new MemoryCacheOptions());
        return new RevocationCache(
            memory,
            new InertScopeFactory(),
            NullLogger<RevocationCache>.Instance);
    }

    /// <summary>
    /// Stand-in for <see cref="IServiceScopeFactory"/> when the warm-from-DB path
    /// inside <see cref="RevocationCache"/> won't be hit. Throws to keep any
    /// inadvertent reach loud.
    /// </summary>
    private sealed class InertScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() =>
            throw new NotImplementedException("Test reached WarmFromDatabaseAsync unexpectedly.");
    }

    /// <summary>
    /// Tiny <see cref="IMediator"/> implementation that dispatches the three
    /// revocation commands to fresh handler instances (each with its own
    /// DbContext, mirroring per-request scoping). Keeps the controller tests
    /// honest — we exercise the real handler logic, not a mock — while staying
    /// off the full ASP.NET pipeline. Only the three commands the controller
    /// actually sends are wired; everything else throws.
    /// </summary>
    private sealed class StubMediator : IMediator
    {
        private readonly RuntimeTokensAdminControllerTests _outer;
        private readonly IRevocationCache _cache;

        public StubMediator(RuntimeTokensAdminControllerTests outer, IRevocationCache cache)
        {
            _outer = outer;
            _cache = cache;
        }

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            object resultObj = request switch
            {
                RevokeTokenCommand cmd =>
                    await new RevokeTokenCommandHandler(_outer.OpenDb(), _cache).Handle(cmd, cancellationToken),
                RevokeAllForRuntimeCommand cmd =>
                    await new RevokeAllForRuntimeCommandHandler(_outer.OpenDb(), _cache).Handle(cmd, cancellationToken),
                RevokeAllForTenantCommand cmd =>
                    await new RevokeAllForTenantCommandHandler(_outer.OpenDb(), _cache).Handle(cmd, cancellationToken),
                _ => throw new NotSupportedException(
                    $"StubMediator was not configured to dispatch {request.GetType().Name}."),
            };
            return (TResponse)resultObj;
        }

        // Unused on this controller, but IMediator wants the full surface. Throw so
        // any future controller-side use of these is loud rather than silent.
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification =>
            throw new NotSupportedException();
    }
}

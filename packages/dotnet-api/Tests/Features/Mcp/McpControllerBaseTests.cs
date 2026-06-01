using System.Security.Claims;
using Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Source.Features.Mcp.Framework;
using Source.Features.Mcp.Models;
using Source.Features.RuntimeTokens.Models;
using Source.Infrastructure;
using Source.Shared;
using Source.Shared.Results;

namespace Api.Tests.Features.Mcp;

/// <summary>
/// Unit coverage for <see cref="McpControllerBase"/> — the auth + force-scope +
/// audit framework every concrete MCP controller inherits. We exercise the base
/// directly via a minimal <see cref="TestMcpController"/> defined inside this
/// file and a real in-memory <see cref="ApplicationDbContext"/> from
/// <see cref="TestDbContextFactory"/>.
///
/// <para>Mirrors the
/// <see cref="Api.Tests.Features.ProjectSecrets.BootstrapEnvControllerTests"/>
/// shape: stamp a <see cref="DefaultHttpContext"/> pre-populated with the
/// RuntimeToken claims, instantiate the controller, call the action directly.
/// We do NOT exercise the JWT bearer middleware here — that's covered in
/// <c>RuntimeTokenServiceTests</c>; here we test what the controller does
/// after the principal has been authenticated.</para>
/// </summary>
public class McpControllerBaseTests
{
    private const string TestServerName = "testmcp";

    /// <summary>
    /// Concrete test controller — the smallest possible thing that exercises the
    /// base class. Exposes a single <c>Test</c> action that delegates to the
    /// supplied handler so individual tests can plug in success / failure /
    /// exception flows.
    /// </summary>
    [McpServer(name: TestServerName, version: "v1")]
    private sealed class TestMcpController : McpControllerBase
    {
        private readonly Func<TestInput?, Task<Result<TestOutput>>> _handler;

        public TestMcpController(
            ApplicationDbContext db,
            ILogger<McpControllerBase> logger,
            McpRateLimiter rateLimiter,
            Func<TestInput?, Task<Result<TestOutput>>> handler)
            : base(db, logger, rateLimiter)
        {
            _handler = handler;
        }

        public Task<IActionResult> Test(TestInput? input, CancellationToken ct) =>
            InvokeAsync("test", input, _handler, ct);

        // Surface the protected resolution helpers for assertions.
        public Guid GetRuntimeIdForTest() => RuntimeId;
        public Guid GetProjectIdForTest() => ProjectId;
        public string GetServerNameForTest() => ServerName;
    }

    private sealed record TestInput(string? ProjectId, string? TenantId, string? RuntimeId, string Title);

    private sealed record TestOutput(string Echo);

    private static (TestMcpController controller, Mock<ILogger<McpControllerBase>> logger) CreateController(
        ApplicationDbContext db,
        Func<TestInput?, Task<Result<TestOutput>>> handler,
        Guid? runtimeIdClaim,
        Guid? projectIdClaim,
        McpRateLimiter? rateLimiter = null)
    {
        var identity = new ClaimsIdentity(authenticationType: "RuntimeToken");
        if (runtimeIdClaim.HasValue)
            identity.AddClaim(new Claim(RuntimeTokenClaimNames.RuntimeId, runtimeIdClaim.Value.ToString()));
        if (projectIdClaim.HasValue)
            identity.AddClaim(new Claim(RuntimeTokenClaimNames.ProjectId, projectIdClaim.Value.ToString()));
        var principal = new ClaimsPrincipal(identity);

        var httpContext = new DefaultHttpContext { User = principal };

        var logger = new Mock<ILogger<McpControllerBase>>();
        var limiter = rateLimiter ?? new McpRateLimiter(
            new SystemClock(),
            NullLogger<McpRateLimiter>.Instance);
        var controller = new TestMcpController(db, logger.Object, limiter, handler)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
        return (controller, logger);
    }

    private static int CountWarnings(Mock<ILogger<McpControllerBase>> logger) =>
        logger.Invocations
            .Count(i => i.Method.Name == nameof(ILogger.Log)
                        && i.Arguments.Count > 0
                        && Equals(i.Arguments[0], LogLevel.Warning));

    // ----------------------------------------------------------------------
    // Forbidden-field strip
    // ----------------------------------------------------------------------

    [Fact]
    public async Task ForbiddenFields_AreStrippedAndWarned_HandlerReceivesScrubbedInput()
    {
        await using var db = TestDbContextFactory.Create();
        TestInput? captured = null;
        Task<Result<TestOutput>> Handler(TestInput? input)
        {
            captured = input;
            return Task.FromResult(Result.Success(new TestOutput("ok")));
        }

        var (controller, logger) = CreateController(
            db, Handler, runtimeIdClaim: Guid.NewGuid(), projectIdClaim: Guid.NewGuid());

        var clientPayload = new TestInput(
            ProjectId: Guid.NewGuid().ToString(),
            TenantId: Guid.NewGuid().ToString(),
            RuntimeId: Guid.NewGuid().ToString(),
            Title: "real work");

        var result = await controller.Test(clientPayload, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();

        captured.Should().NotBeNull();
        captured!.Title.Should().Be("real work", "non-forbidden fields must pass through untouched");
        captured.ProjectId.Should().BeNull("client-supplied scope must be zeroed before the handler runs");
        captured.TenantId.Should().BeNull();
        captured.RuntimeId.Should().BeNull();

        // One warning per stripped field that was non-null on entry.
        CountWarnings(logger).Should().Be(3,
            "exactly one warning per forbidden field that actually held a client value");
    }

    [Fact]
    public async Task ForbiddenFields_NullValues_DoNotEmitWarnings()
    {
        await using var db = TestDbContextFactory.Create();
        Task<Result<TestOutput>> Handler(TestInput? input) =>
            Task.FromResult(Result.Success(new TestOutput("ok")));

        var (controller, logger) = CreateController(
            db, Handler, runtimeIdClaim: Guid.NewGuid(), projectIdClaim: Guid.NewGuid());

        var clean = new TestInput(ProjectId: null, TenantId: null, RuntimeId: null, Title: "ok");
        await controller.Test(clean, CancellationToken.None);

        CountWarnings(logger).Should().Be(0,
            "the strip is silent when no forbidden field carried a client value — log noise is reserved for genuine misuse");
    }

    // ----------------------------------------------------------------------
    // Audit row
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Success_WritesAuditRow_WithStatusSuccess()
    {
        await using var db = TestDbContextFactory.Create();
        Task<Result<TestOutput>> Handler(TestInput? input) =>
            Task.FromResult(Result.Success(new TestOutput("yes")));

        var runtimeId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var (controller, _) = CreateController(db, Handler, runtimeId, projectId);

        var input = new TestInput(null, null, null, "hello");
        var result = await controller.Test(input, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var envelope = ok.Value.Should().BeOfType<McpResponse<TestOutput>>().Subject;
        envelope.Result.Should().NotBeNull();
        envelope.Result!.Echo.Should().Be("yes");
        envelope.Error.Should().BeNull();

        var audit = await db.McpCalls.SingleAsync();
        audit.RuntimeId.Should().Be(runtimeId);
        audit.ServerName.Should().Be(TestServerName);
        audit.Method.Should().Be("test");
        audit.Status.Should().Be(McpCallStatus.Success);
        audit.ErrorCode.Should().BeNull();
        audit.RequestSizeBytes.Should().BeGreaterThan(0,
            "we serialise the input to estimate its byte cost — a non-null record must have non-zero size");
        audit.ResponseSizeBytes.Should().BeGreaterThan(0);
        audit.DurationMs.Should().BeGreaterOrEqualTo(0);
    }

    [Fact]
    public async Task Failure_WritesAuditRow_WithStatusClientErrorAndPropagatesErrorCode()
    {
        await using var db = TestDbContextFactory.Create();
        Task<Result<TestOutput>> Handler(TestInput? input) =>
            Task.FromResult(Result.Failure<TestOutput>("test_error"));

        var (controller, _) = CreateController(db, Handler, Guid.NewGuid(), Guid.NewGuid());

        var result = await controller.Test(new TestInput(null, null, null, "x"), CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var envelope = ok.Value.Should().BeOfType<McpResponse<TestOutput>>().Subject;
        envelope.Result.Should().BeNull();
        envelope.Error.Should().NotBeNull();
        envelope.Error!.Code.Should().Be("test_error");
        envelope.Error.Retryable.Should().BeFalse();

        var audit = await db.McpCalls.SingleAsync();
        audit.Status.Should().Be(McpCallStatus.ClientError,
            "MVP buckets all Result.Failure as ClientError; subclasses can refine if needed");
        audit.ErrorCode.Should().Be("test_error");
    }

    [Fact]
    public async Task UnhandledException_WritesAuditRow_WithStatusServerErrorAndGenericEnvelope()
    {
        await using var db = TestDbContextFactory.Create();
        Task<Result<TestOutput>> Handler(TestInput? input) =>
            throw new InvalidOperationException("boom: secret env=foo");

        var (controller, logger) = CreateController(db, Handler, Guid.NewGuid(), Guid.NewGuid());

        var result = await controller.Test(new TestInput(null, null, null, "x"), CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var envelope = ok.Value.Should().BeOfType<McpResponse<TestOutput>>().Subject;
        envelope.Result.Should().BeNull();
        envelope.Error.Should().NotBeNull();
        envelope.Error!.Code.Should().Be("internal_error",
            "the inner exception message must never leak to the wire");
        envelope.Error.Message.Should().Be("Internal server error");
        envelope.Error.Retryable.Should().BeFalse();

        var audit = await db.McpCalls.SingleAsync();
        audit.Status.Should().Be(McpCallStatus.ServerError);
        audit.ErrorCode.Should().Be("internal_error");

        // Error log captured the exception so an operator can correlate.
        logger.Invocations
            .Count(i => i.Method.Name == nameof(ILogger.Log)
                        && i.Arguments.Count > 0
                        && Equals(i.Arguments[0], LogLevel.Error))
            .Should().Be(1);
    }

    // ----------------------------------------------------------------------
    // Audit-write resilience
    // ----------------------------------------------------------------------

    [Fact]
    public async Task AuditWriteFailure_DoesNotFailTheCall()
    {
        // Use a wrapping context that throws on SaveChangesAsync. The caller
        // must still see a successful envelope — losing the audit row is bad,
        // failing the actual response because audit failed would be worse.
        await using var realDb = TestDbContextFactory.Create();
        await using var failingDb = new ThrowOnSaveContext(realDb);

        Task<Result<TestOutput>> Handler(TestInput? input) =>
            Task.FromResult(Result.Success(new TestOutput("ok")));

        var (controller, logger) = CreateController(
            failingDb, Handler, Guid.NewGuid(), Guid.NewGuid());

        var result = await controller.Test(new TestInput(null, null, null, "x"), CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var envelope = ok.Value.Should().BeOfType<McpResponse<TestOutput>>().Subject;
        envelope.Error.Should().BeNull("audit failure must not corrupt the response envelope");
        envelope.Result!.Echo.Should().Be("ok");

        // Error log captured the audit-write failure.
        logger.Invocations
            .Count(i => i.Method.Name == nameof(ILogger.Log)
                        && i.Arguments.Count > 0
                        && Equals(i.Arguments[0], LogLevel.Error))
            .Should().BeGreaterOrEqualTo(1, "audit-write failure must surface to ops via an error log");
    }

    /// <summary>
    /// Throws on <see cref="SaveChangesAsync(CancellationToken)"/> while
    /// otherwise behaving like the wrapped real context. We can't override the
    /// in-memory DB to fail saves, so we wrap the model and intercept the call.
    /// </summary>
    private sealed class ThrowOnSaveContext : ApplicationDbContext
    {
        public ThrowOnSaveContext(ApplicationDbContext source)
            : base(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options)
        {
            // Source is intentionally unused — we rely on our own underlying
            // in-memory DB for entity tracking and just throw on save.
            _ = source;
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            throw new DbUpdateException("simulated audit write failure");
    }

    // ----------------------------------------------------------------------
    // Reflection cache (smoke)
    // ----------------------------------------------------------------------

    [Fact]
    public async Task ForbiddenFieldsCache_HandlesRepeatCalls_WithoutBehaviourChange()
    {
        // The reflection cache is a private static ConcurrentDictionary keyed
        // by Type — exercising it directly would require InternalsVisibleTo. We
        // instead cover the contract: making two calls with the same TIn
        // produces identical strip behaviour. (Manual review of the impl
        // confirms the cache is keyed once per Type.)
        await using var db = TestDbContextFactory.Create();
        Task<Result<TestOutput>> Handler(TestInput? input) =>
            Task.FromResult(Result.Success(new TestOutput("ok")));

        var (controller, _) = CreateController(db, Handler, Guid.NewGuid(), Guid.NewGuid());

        var first = new TestInput(ProjectId: "x", TenantId: null, RuntimeId: null, Title: "one");
        var second = new TestInput(ProjectId: "y", TenantId: null, RuntimeId: null, Title: "two");

        await controller.Test(first, CancellationToken.None);
        await controller.Test(second, CancellationToken.None);

        first.ProjectId.Should().BeNull();
        second.ProjectId.Should().BeNull();

        // Two audit rows — the framework's bookkeeping kept up with the cache reuse.
        (await db.McpCalls.CountAsync()).Should().Be(2);
    }

    // ----------------------------------------------------------------------
    // Defensive claim resolution
    // ----------------------------------------------------------------------

    [Fact]
    public async Task MissingRuntimeIdClaim_Throws_BeforeRunningHandler()
    {
        await using var db = TestDbContextFactory.Create();
        var handlerCalled = false;
        Task<Result<TestOutput>> Handler(TestInput? input)
        {
            handlerCalled = true;
            return Task.FromResult(Result.Success(new TestOutput("ok")));
        }

        var (controller, _) = CreateController(
            db, Handler,
            runtimeIdClaim: null,                  // missing
            projectIdClaim: Guid.NewGuid());

        var act = () => controller.Test(new TestInput(null, null, null, "x"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*rt_runtime*");
        handlerCalled.Should().BeFalse(
            "a malformed principal must short-circuit before the handler runs");
    }

    [Fact]
    public async Task MissingProjectIdClaim_Throws_BeforeRunningHandler()
    {
        await using var db = TestDbContextFactory.Create();
        Task<Result<TestOutput>> Handler(TestInput? input) =>
            Task.FromResult(Result.Success(new TestOutput("ok")));

        var (controller, _) = CreateController(
            db, Handler,
            runtimeIdClaim: Guid.NewGuid(),
            projectIdClaim: null);                 // missing

        var act = () => controller.Test(new TestInput(null, null, null, "x"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*rt_project*");
    }

    // ----------------------------------------------------------------------
    // Server-name resolution
    // ----------------------------------------------------------------------

    [Fact]
    public async Task ServerName_IsResolvedFromMcpServerAttribute()
    {
        await using var db = TestDbContextFactory.Create();
        Task<Result<TestOutput>> Handler(TestInput? input) =>
            Task.FromResult(Result.Success(new TestOutput("ok")));

        var (controller, _) = CreateController(db, Handler, Guid.NewGuid(), Guid.NewGuid());

        controller.GetServerNameForTest().Should().Be(TestServerName);
    }

    // ----------------------------------------------------------------------
    // Rate limiting (Card 6)
    // ----------------------------------------------------------------------

    [Fact]
    public async Task RateLimiter_BurstBeyondCapacity_ReturnsRateLimitedEnvelopeAndAuditRow()
    {
        await using var db = TestDbContextFactory.Create();
        var clock = new FakeClock();
        var limiter = new McpRateLimiter(clock, NullLogger<McpRateLimiter>.Instance);

        var handlerCalls = 0;
        Task<Result<TestOutput>> Handler(TestInput? input)
        {
            handlerCalls++;
            return Task.FromResult(Result.Success(new TestOutput("ok")));
        }

        var (controller, _) = CreateController(
            db, Handler, Guid.NewGuid(), Guid.NewGuid(), rateLimiter: limiter);

        // Defaults: capacity = 60. Drain the bucket.
        for (int i = 0; i < 60; i++)
        {
            var result = await controller.Test(new TestInput(null, null, null, "x"), CancellationToken.None);
            var ok = result.Should().BeOfType<OkObjectResult>().Subject;
            var envelope = ok.Value.Should().BeOfType<McpResponse<TestOutput>>().Subject;
            envelope.Error.Should().BeNull($"call {i + 1} should still be inside the burst budget");
        }

        // 61st call — bucket empty, no time elapsed since the clock is frozen.
        var deniedResult = await controller.Test(new TestInput(null, null, null, "x"), CancellationToken.None);
        var deniedOk = deniedResult.Should().BeOfType<OkObjectResult>().Subject;
        var deniedEnvelope = deniedOk.Value.Should().BeOfType<McpResponse<TestOutput>>().Subject;
        deniedEnvelope.Result.Should().BeNull();
        deniedEnvelope.Error.Should().NotBeNull();
        deniedEnvelope.Error!.Code.Should().Be("rate_limit_exceeded");
        deniedEnvelope.Error.Retryable.Should().BeTrue();
        deniedEnvelope.Error.Details.Should().ContainKey("retryAfterMs");

        handlerCalls.Should().Be(60, "handler must not run for the rate-limited 61st call");

        // Audit rows: 60 Success + 1 RateLimited = 61.
        var audits = await db.McpCalls.ToListAsync();
        audits.Count.Should().Be(61);
        audits.Count(a => a.Status == McpCallStatus.Success).Should().Be(60);
        var rateLimited = audits.Single(a => a.Status == McpCallStatus.RateLimited);
        rateLimited.ErrorCode.Should().Be("rate_limit_exceeded");
    }

    [Fact]
    public async Task RateLimiter_AfterClockAdvance_NextCallSucceedsAgain()
    {
        await using var db = TestDbContextFactory.Create();
        var clock = new FakeClock();
        var limiter = new McpRateLimiter(clock, NullLogger<McpRateLimiter>.Instance);

        Task<Result<TestOutput>> Handler(TestInput? input) =>
            Task.FromResult(Result.Success(new TestOutput("ok")));

        var (controller, _) = CreateController(
            db, Handler, Guid.NewGuid(), Guid.NewGuid(), rateLimiter: limiter);

        // Drain to deny.
        for (int i = 0; i < 60; i++)
            await controller.Test(new TestInput(null, null, null, "x"), CancellationToken.None);

        var deniedResult = await controller.Test(new TestInput(null, null, null, "x"), CancellationToken.None);
        var deniedEnvelope = ((OkObjectResult)deniedResult).Value.Should().BeOfType<McpResponse<TestOutput>>().Subject;
        deniedEnvelope.Error!.Code.Should().Be("rate_limit_exceeded");

        // Advance the clock 1 second — refill rate is 1 token/s by default.
        clock.Advance(TimeSpan.FromSeconds(1));

        var allowedResult = await controller.Test(new TestInput(null, null, null, "x"), CancellationToken.None);
        var allowedEnvelope = ((OkObjectResult)allowedResult).Value.Should().BeOfType<McpResponse<TestOutput>>().Subject;
        allowedEnvelope.Error.Should().BeNull("one second of refill restores one token");

        // After the refill we expect: 60 Success (initial drain) + 1 RateLimited
        // (the denial) + 1 Success (the post-refill call) = 62 audit rows.
        var audits = await db.McpCalls.ToListAsync();
        audits.Count.Should().Be(62);
        audits.Count(a => a.Status == McpCallStatus.Success).Should().Be(61);
        audits.Count(a => a.Status == McpCallStatus.RateLimited).Should().Be(1);
    }
}

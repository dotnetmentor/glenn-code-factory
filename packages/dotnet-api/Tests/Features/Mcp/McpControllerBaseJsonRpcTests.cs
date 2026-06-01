using System.Security.Claims;
using System.Text.Json;
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
/// Unit coverage for the JSON-RPC 2.0 / MCP Streamable HTTP dispatcher added to
/// <see cref="McpControllerBase"/> by the mcp-streamable-http-transport spec.
///
/// <para>The dispatcher routes <c>initialize</c> / <c>tools/list</c> /
/// <c>tools/call</c> at the controller's base route (<c>POST /mcp/{name}/{ver}</c>)
/// and delegates <c>tools/call</c> back to the existing
/// <c>[HttpPost("toolName")]</c> actions via reflection — so every claim resolution,
/// forbidden-field strip, audit row, and rate-limit decision in
/// <see cref="McpControllerBase.InvokeAsync{TIn,TOut}"/> still fires identically.
/// These tests pin the JSON-RPC envelope mapping (success / error / methodnotfound /
/// invalidparams) and that the chained dispatch preserves the framework's
/// security/audit invariants end-to-end.</para>
///
/// <para><b>Shape.</b> Mirrors <see cref="McpControllerBaseTests"/>: a private
/// sealed concrete controller stamped with the right attributes, a stamped
/// <see cref="DefaultHttpContext"/> with the runtime claims, the action called
/// directly. We do NOT exercise the JWT bearer middleware — separately covered
/// by <c>RuntimeTokenServiceTests</c>.</para>
/// </summary>
public class McpControllerBaseJsonRpcTests
{
    private const string TestServerName = "test-jsonrpc";
    private const string TestServerVersion = "v1";
    private const string TestToolName = "doThing";

    /// <summary>
    /// Concrete test controller — minimum needed for the dispatcher to discover a
    /// <c>tools/call</c> target via reflection. Exposes one
    /// <c>[HttpPost("doThing")]</c> action delegating to a per-test handler so
    /// individual cases can plug in success / failure / exception flows.
    /// </summary>
    [McpServer(name: TestServerName, version: TestServerVersion)]
    private sealed class JsonRpcTestController : McpControllerBase
    {
        private readonly Func<JsonRpcTestInput?, Task<Result<JsonRpcTestOutput>>> _handler;

        public JsonRpcTestController(
            ApplicationDbContext db,
            ILogger<McpControllerBase> logger,
            McpRateLimiter rateLimiter,
            Func<JsonRpcTestInput?, Task<Result<JsonRpcTestOutput>>> handler)
            : base(db, logger, rateLimiter)
        {
            _handler = handler;
        }

        // The dispatcher's tool-catalog reflection requires a [HttpPost("name")]
        // attribute. Attribute is what makes this discoverable, not the method
        // name — the action could be named anything.
        [HttpPost("doThing")]
        public Task<IActionResult> DoThing(
            [FromBody] JsonRpcTestInput? input,
            CancellationToken ct) =>
            InvokeAsync(TestToolName, input, _handler, ct);
    }

    private sealed record JsonRpcTestInput(string? ProjectId, string Title);
    private sealed record JsonRpcTestOutput(string Echo);

    private static (JsonRpcTestController controller, Mock<ILogger<McpControllerBase>> logger) CreateController(
        ApplicationDbContext db,
        Func<JsonRpcTestInput?, Task<Result<JsonRpcTestOutput>>> handler,
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
        var controller = new JsonRpcTestController(db, logger.Object, limiter, handler)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
        return (controller, logger);
    }

    /// <summary>
    /// Build a JSON-RPC request envelope from raw JSON, mimicking what the SDK's
    /// MCP client sends. <see cref="JsonDocument"/> is owned by the caller — we
    /// keep it open so the embedded <see cref="JsonElement"/> values stay
    /// addressable for the duration of the controller call.
    /// </summary>
    private static (McpJsonRpcRequest request, JsonDocument doc) BuildRequest(string json)
    {
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var idEl = root.TryGetProperty("id", out var id) ? id.Clone() : (JsonElement?)null;
        var paramsEl = root.TryGetProperty("params", out var p) ? p.Clone() : (JsonElement?)null;
        var req = new McpJsonRpcRequest(
            JsonRpc: root.GetProperty("jsonrpc").GetString()!,
            Id: idEl,
            Method: root.TryGetProperty("method", out var m) ? m.GetString()! : "",
            Params: paramsEl);
        return (req, doc);
    }

    // ------------------------------------------------------------------------
    // initialize
    // ------------------------------------------------------------------------

    [Fact]
    public async Task Initialize_ReturnsProtocolVersionAndServerInfo()
    {
        await using var db = TestDbContextFactory.Create();
        var (controller, _) = CreateController(
            db, _ => Task.FromResult(Result.Success(new JsonRpcTestOutput("ok"))),
            Guid.NewGuid(), Guid.NewGuid());

        var (req, doc) = BuildRequest("""{"jsonrpc":"2.0","id":1,"method":"initialize"}""");
        using var _ = doc;

        var result = await controller.HandleJsonRpc(req, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var success = ok.Value.Should().BeOfType<McpJsonRpcSuccessResponse>().Subject;
        success.JsonRpc.Should().Be("2.0");
        var init = success.Result.Should().BeOfType<McpInitializeResult>().Subject;
        init.ProtocolVersion.Should().Be(McpJsonRpcConstants.ProtocolVersion,
            "advertised MCP protocol version is wire contract — bumps must be deliberate");
        init.ServerInfo.Name.Should().Be(TestServerName);
        init.ServerInfo.Version.Should().Be(TestServerVersion);
        init.Capabilities.Tools.Should().NotBeNull("we claim the 'tools' capability group");
    }

    // ------------------------------------------------------------------------
    // tools/list
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ToolsList_ReturnsCatalog_WithGeneratedJsonSchema()
    {
        await using var db = TestDbContextFactory.Create();
        var (controller, _) = CreateController(
            db, _ => Task.FromResult(Result.Success(new JsonRpcTestOutput("ok"))),
            Guid.NewGuid(), Guid.NewGuid());

        var (req, doc) = BuildRequest("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        using var _ = doc;

        var result = await controller.HandleJsonRpc(req, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var success = ok.Value.Should().BeOfType<McpJsonRpcSuccessResponse>().Subject;
        var list = success.Result.Should().BeOfType<McpToolsListResult>().Subject;

        list.Tools.Should().ContainSingle(
            "the test controller exposes exactly one [HttpPost(\"...\")] action");
        var tool = list.Tools[0];
        tool.Name.Should().Be(TestToolName);
        // The schema is a Dictionary<string,object?> built by McpJsonSchemaGenerator —
        // re-serialise to JSON and round-trip to JsonElement for inspection.
        var schemaJson = JsonSerializer.SerializeToElement(tool.InputSchema);
        schemaJson.GetProperty("type").GetString().Should().Be("object");
        var properties = schemaJson.GetProperty("properties");
        properties.TryGetProperty("title", out var titleProp).Should().BeTrue(
            "non-nullable record properties are emitted in the schema");
        titleProp.GetProperty("type").GetString().Should().Be("string");
        schemaJson.GetProperty("required").EnumerateArray()
            .Select(e => e.GetString())
            .Should().Contain("title",
                "non-nullable record params are required by default");
    }

    // ------------------------------------------------------------------------
    // tools/call — happy path
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ToolsCall_InvokesAction_AndReturnsTextContentBlock()
    {
        await using var db = TestDbContextFactory.Create();
        JsonRpcTestInput? captured = null;
        Task<Result<JsonRpcTestOutput>> Handler(JsonRpcTestInput? input)
        {
            captured = input;
            return Task.FromResult(Result.Success(new JsonRpcTestOutput($"echo:{input?.Title}")));
        }

        var (controller, _) = CreateController(db, Handler, Guid.NewGuid(), Guid.NewGuid());

        var (req, doc) = BuildRequest("""
            {"jsonrpc":"2.0","id":42,"method":"tools/call",
             "params":{"name":"doThing","arguments":{"title":"hello-world"}}}
            """);
        using var _ = doc;

        var result = await controller.HandleJsonRpc(req, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var success = ok.Value.Should().BeOfType<McpJsonRpcSuccessResponse>().Subject;
        var callResult = success.Result.Should().BeOfType<McpToolsCallResult>().Subject;
        callResult.IsError.Should().BeFalse();
        callResult.Content.Should().HaveCount(1);
        callResult.Content[0].Type.Should().Be("text");
        // The serialised payload is the underlying handler's TOut — pin the echo.
        var payload = JsonDocument.Parse(callResult.Content[0].Text);
        payload.RootElement.GetProperty("echo").GetString().Should().Be("echo:hello-world");

        captured.Should().NotBeNull();
        captured!.Title.Should().Be("hello-world");
    }

    // ------------------------------------------------------------------------
    // tools/call — force-scoping invariant from spec card 2 (a)
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ToolsCall_StripsClientSuppliedProjectId_BeforeHandlerRuns()
    {
        await using var db = TestDbContextFactory.Create();
        JsonRpcTestInput? captured = null;
        Task<Result<JsonRpcTestOutput>> Handler(JsonRpcTestInput? input)
        {
            captured = input;
            return Task.FromResult(Result.Success(new JsonRpcTestOutput("ok")));
        }

        var claimsProjectId = Guid.NewGuid();
        var (controller, _) = CreateController(db, Handler, Guid.NewGuid(), claimsProjectId);

        var hostileOtherTenantProject = Guid.NewGuid();
        // String.Concat over an interpolated raw string here — JSON's `}}}` close
        // sequence collides with the C# 11 raw-interpolation hole syntax (`{{...}}`).
        var json = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\","
                 + "\"params\":{\"name\":\"doThing\",\"arguments\":{\"projectId\":\""
                 + hostileOtherTenantProject + "\",\"title\":\"x\"}}}";
        var (req, doc) = BuildRequest(json);
        using var _ = doc;

        var result = await controller.HandleJsonRpc(req, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();

        captured.Should().NotBeNull();
        captured!.ProjectId.Should().BeNull(
            "the spec invariant: client-supplied projectId must be zeroed before the handler runs, " +
            "regardless of which transport (REST or JSON-RPC) carried it");
        captured.Title.Should().Be("x", "non-scope fields must still pass through");

        // Audit row should reflect the framework-trusted runtime/server, not the
        // hostile project id.
        var audit = await db.McpCalls.SingleAsync();
        audit.ServerName.Should().Be(TestServerName);
        audit.Method.Should().Be(TestToolName);
        audit.Status.Should().Be(McpCallStatus.Success);
    }

    // ------------------------------------------------------------------------
    // tools/call — failure mapping (spec card 1)
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ToolsCall_HandlerFailure_MapsToJsonRpcError_WithEnvelopeInData()
    {
        await using var db = TestDbContextFactory.Create();
        Task<Result<JsonRpcTestOutput>> Handler(JsonRpcTestInput? _) =>
            Task.FromResult(Result.Failure<JsonRpcTestOutput>("invalid_input"));

        var (controller, _) = CreateController(db, Handler, Guid.NewGuid(), Guid.NewGuid());

        var (req, doc) = BuildRequest("""
            {"jsonrpc":"2.0","id":7,"method":"tools/call",
             "params":{"name":"doThing","arguments":{"title":"x"}}}
            """);
        using var _ = doc;

        var result = await controller.HandleJsonRpc(req, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var err = ok.Value.Should().BeOfType<McpJsonRpcErrorResponse>().Subject;
        err.Error.Code.Should().Be(McpJsonRpcConstants.McpEnvelopeError,
            "application-level handler failures map to our reserved -32001 code so the SDK can branch on it");
        err.Error.Message.Should().Be("invalid_input");
        var mcpErr = err.Error.Data.Should().BeOfType<McpError>().Subject;
        mcpErr.Code.Should().Be("invalid_input");
        mcpErr.Retryable.Should().BeFalse();
    }

    [Fact]
    public async Task ToolsCall_UnknownTool_ReturnsInvalidParams()
    {
        await using var db = TestDbContextFactory.Create();
        var (controller, _) = CreateController(
            db, _ => Task.FromResult(Result.Success(new JsonRpcTestOutput("ok"))),
            Guid.NewGuid(), Guid.NewGuid());

        var (req, doc) = BuildRequest("""
            {"jsonrpc":"2.0","id":1,"method":"tools/call",
             "params":{"name":"nonexistent","arguments":{}}}
            """);
        using var _ = doc;

        var result = await controller.HandleJsonRpc(req, CancellationToken.None);
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var err = ok.Value.Should().BeOfType<McpJsonRpcErrorResponse>().Subject;
        err.Error.Code.Should().Be(McpJsonRpcConstants.InvalidParams);
        err.Error.Message.Should().Contain("nonexistent");
    }

    [Fact]
    public async Task ToolsCall_MissingNameField_ReturnsInvalidParams()
    {
        await using var db = TestDbContextFactory.Create();
        var (controller, _) = CreateController(
            db, _ => Task.FromResult(Result.Success(new JsonRpcTestOutput("ok"))),
            Guid.NewGuid(), Guid.NewGuid());

        var (req, doc) = BuildRequest("""
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{}}
            """);
        using var _ = doc;

        var result = await controller.HandleJsonRpc(req, CancellationToken.None);
        var err = ((OkObjectResult)result).Value
            .Should().BeOfType<McpJsonRpcErrorResponse>().Subject;
        err.Error.Code.Should().Be(McpJsonRpcConstants.InvalidParams);
    }

    // ------------------------------------------------------------------------
    // method dispatch
    // ------------------------------------------------------------------------

    [Fact]
    public async Task UnknownMethod_ReturnsMethodNotFound()
    {
        await using var db = TestDbContextFactory.Create();
        var (controller, _) = CreateController(
            db, _ => Task.FromResult(Result.Success(new JsonRpcTestOutput("ok"))),
            Guid.NewGuid(), Guid.NewGuid());

        var (req, doc) = BuildRequest("""{"jsonrpc":"2.0","id":1,"method":"foo/bar"}""");
        using var _ = doc;

        var result = await controller.HandleJsonRpc(req, CancellationToken.None);
        var err = ((OkObjectResult)result).Value
            .Should().BeOfType<McpJsonRpcErrorResponse>().Subject;
        err.Error.Code.Should().Be(McpJsonRpcConstants.MethodNotFound);
    }

    [Fact]
    public async Task InitializedNotification_ReturnsBenignSuccess()
    {
        await using var db = TestDbContextFactory.Create();
        var (controller, _) = CreateController(
            db, _ => Task.FromResult(Result.Success(new JsonRpcTestOutput("ok"))),
            Guid.NewGuid(), Guid.NewGuid());

        var (req, doc) = BuildRequest("""{"jsonrpc":"2.0","id":1,"method":"notifications/initialized"}""");
        using var _ = doc;

        var result = await controller.HandleJsonRpc(req, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();
        ((OkObjectResult)result).Value.Should().BeOfType<McpJsonRpcSuccessResponse>(
            "spec says no response is required, but a benign empty success is what SDK clients expect");
    }

    [Fact]
    public async Task MalformedRequest_ReturnsInvalidRequest()
    {
        await using var db = TestDbContextFactory.Create();
        var (controller, _) = CreateController(
            db, _ => Task.FromResult(Result.Success(new JsonRpcTestOutput("ok"))),
            Guid.NewGuid(), Guid.NewGuid());

        // No method field — malformed envelope.
        var bogus = new McpJsonRpcRequest(JsonRpc: "2.0", Id: null, Method: "", Params: null);
        var result = await controller.HandleJsonRpc(bogus, CancellationToken.None);
        var err = ((OkObjectResult)result).Value
            .Should().BeOfType<McpJsonRpcErrorResponse>().Subject;
        err.Error.Code.Should().Be(McpJsonRpcConstants.InvalidRequest);
    }

    // ------------------------------------------------------------------------
    // Spec card 2 invariant (b): audit row is written on the JSON-RPC path,
    // identically to the REST path.
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ToolsCall_WritesAuditRow_WithFrameworkServerAndMethodNames()
    {
        await using var db = TestDbContextFactory.Create();
        Task<Result<JsonRpcTestOutput>> Handler(JsonRpcTestInput? _) =>
            Task.FromResult(Result.Success(new JsonRpcTestOutput("ok")));

        var runtimeId = Guid.NewGuid();
        var (controller, _) = CreateController(db, Handler, runtimeId, Guid.NewGuid());

        var (req, doc) = BuildRequest("""
            {"jsonrpc":"2.0","id":1,"method":"tools/call",
             "params":{"name":"doThing","arguments":{"title":"x"}}}
            """);
        using var _ = doc;

        await controller.HandleJsonRpc(req, CancellationToken.None);

        var audit = await db.McpCalls.SingleAsync();
        audit.RuntimeId.Should().Be(runtimeId, "audit must reflect the JWT-trusted runtime id");
        audit.ServerName.Should().Be(TestServerName);
        audit.Method.Should().Be(TestToolName, "the audit method is the MCP tool name, not 'tools/call'");
        audit.Status.Should().Be(McpCallStatus.Success);
    }

    // ------------------------------------------------------------------------
    // Spec card 2 invariant (c): rate-limit still fires on the JSON-RPC path
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ToolsCall_RateLimitExceeded_SurfacesAsEnvelopeErrorViaJsonRpc()
    {
        await using var db = TestDbContextFactory.Create();
        var clock = new FakeClock();
        var limiter = new McpRateLimiter(clock, NullLogger<McpRateLimiter>.Instance);

        Task<Result<JsonRpcTestOutput>> Handler(JsonRpcTestInput? _) =>
            Task.FromResult(Result.Success(new JsonRpcTestOutput("ok")));

        var (controller, _) = CreateController(
            db, Handler, Guid.NewGuid(), Guid.NewGuid(), rateLimiter: limiter);

        // Defaults: capacity = 60. Drain it via the JSON-RPC path.
        for (int i = 0; i < 60; i++)
        {
            var (req, doc) = BuildRequest("""
                {"jsonrpc":"2.0","id":1,"method":"tools/call",
                 "params":{"name":"doThing","arguments":{"title":"x"}}}
                """);
            using var _ = doc;
            var ok = await controller.HandleJsonRpc(req, CancellationToken.None);
            ok.Should().BeOfType<OkObjectResult>();
        }

        var (denyReq, denyDoc) = BuildRequest("""
            {"jsonrpc":"2.0","id":1,"method":"tools/call",
             "params":{"name":"doThing","arguments":{"title":"x"}}}
            """);
        using var __ = denyDoc;
        var denied = await controller.HandleJsonRpc(denyReq, CancellationToken.None);

        // The rate-limit envelope is surfaced as a JSON-RPC error so the SDK can
        // see it without inspecting the success result.
        var err = ((OkObjectResult)denied).Value
            .Should().BeOfType<McpJsonRpcErrorResponse>().Subject;
        err.Error.Code.Should().Be(McpJsonRpcConstants.McpEnvelopeError);
        var mcpErr = err.Error.Data.Should().BeOfType<McpError>().Subject;
        mcpErr.Code.Should().Be("rate_limit_exceeded");
        mcpErr.Retryable.Should().BeTrue();
        mcpErr.Details.Should().ContainKey("retryAfterMs");
    }
}

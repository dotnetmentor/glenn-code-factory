using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Source.Features.Mcp.Models;
using Source.Features.RuntimeTokens.Models;
using Source.Infrastructure;
using Source.Infrastructure.Extensions;
using Source.Shared.Results;

namespace Source.Features.Mcp.Framework;

/// <summary>
/// Common base for every MCP controller. Centralises authentication, force-scoping
/// (server-side derivation of the caller's <see cref="ProjectId"/> from the
/// RuntimeToken claim, never from the request body), and audit-row writing. A
/// concrete MCP controller (e.g. the kanban MCP in spec 15 Card 3) inherits this,
/// stamps <see cref="McpServerAttribute"/> on the class, and exposes one HTTP
/// action per MCP method that delegates to <see cref="InvokeAsync{TIn,TOut}"/>.
///
/// <para><b>Why one base class, no interfaces.</b> Per the user's standing
/// directive (spec 15 framing): the MCP framework is one base class. We evaluate
/// extracting interfaces only when the second concrete MCP shows up and the
/// shared surface starts to feel cramped. Today, the only consumer is one
/// controller; abstractions would be guesswork.</para>
///
/// <para><b>Security boundary.</b> This class is the single place where the
/// caller's project scope is established. Sub-classes never read
/// <c>projectId</c> from a request body — <see cref="ProjectId"/> is resolved
/// from the JWT claim and is the only value the framework trusts. The
/// forbidden-field strip in <see cref="InvokeAsync{TIn,TOut}"/> defends the
/// invariant by zeroing any client-supplied <c>projectId</c> / <c>tenantId</c>
/// / <c>runtimeId</c> field on the input record before the handler runs.</para>
///
/// <para><b>Auth.</b> Gated on <c>[Authorize(AuthenticationSchemes = "RuntimeToken")]</c>
/// — the <see cref="RuntimeTokenAuthenticationDefaults.SchemeName"/> scheme
/// registered in <see cref="AuthenticationExtensions.AddRuntimeTokenAuthScheme"/>.
/// Signature, lifetime, issuer, audience, and revocation are all verified by the
/// JWT bearer middleware before any action on a derived controller runs.
/// Mismatched / malformed claims past that point throw
/// <see cref="InvalidOperationException"/> from <see cref="ResolveClaims"/> —
/// defensive only, would already have been a 401 from middleware in production.</para>
///
/// <para><b>Two transports, one set of actions (mcp-streamable-http-transport
/// spec).</b> Every <c>[HttpPost("toolName")]</c> action on a derived controller
/// is reachable two ways: directly at <c>POST /api/mcp/{server}/{ver}/{toolName}</c>
/// (legacy REST surface, easy to curl by hand for ops smoke), and via JSON-RPC
/// at the controller's <i>base</i> route <c>POST /api/mcp/{server}/{ver}</c> through
/// the dispatcher <see cref="HandleJsonRpc"/> at the bottom of this file. The
/// JSON-RPC path is what the daemon MCP client speaks (MCP Streamable HTTP). The
/// REST path is purely a smoke-test convenience — if you're adding a new MCP
/// tool, define <c>[HttpPost("toolName")]</c> + <see cref="InvokeAsync{TIn,TOut}"/>
/// once and both transports light up automatically. <b>Do not</b> add a separate
/// JSON-RPC handler or duplicate the business logic — the dispatcher
/// reflection-invokes the same action through the same <see cref="InvokeAsync{TIn,TOut}"/>
/// path, so claim resolution, forbidden-field strip, audit, and rate-limit fire
/// identically on both surfaces.</para>
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = RuntimeTokenAuthenticationDefaults.SchemeName)]
public abstract class McpControllerBase : ControllerBase
{
    /// <summary>
    /// Per-type cache of forbidden-field <see cref="PropertyInfo"/> arrays. Reflecting
    /// over a <typeparamref name="TIn"/> is cheap individually but adds up across
    /// every MCP call — the framework caches the result the first time we see a
    /// given input type and reuses it forever after.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> ForbiddenFieldsCache = new();

    /// <summary>
    /// Per-type cache of the <see cref="McpServerAttribute"/> name resolved from
    /// the concrete controller. Reflection happens once per controller type per
    /// process.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, string> ServerNameCache = new();

    /// <summary>
    /// Per-(controllerType, mcpMethodName) cache of the resolved rate-limit
    /// budget. The MCP method name (string passed into <see cref="InvokeAsync{TIn,TOut}"/>)
    /// is the key — not the C# method name — so a controller that maps the same
    /// MCP method to different actions per overload still resolves to the same
    /// budget. Reflection (looking up the action's <see cref="MethodInfo"/> and
    /// reading <see cref="McpMethodRateLimitAttribute"/>) happens once per
    /// (controller, method) pair per process.
    /// </summary>
    private static readonly ConcurrentDictionary<(Type, string), (int Capacity, double RefillPerSecond)>
        RateLimitCache = new();

    /// <summary>
    /// Per-controller-type cache of the JSON-RPC tool catalog: every
    /// <c>[HttpPost("mcpMethod")]</c> action on the concrete controller mapped to
    /// its input type, MethodInfo, and pre-built JSON Schema. Reflected once per
    /// controller type on first <c>tools/list</c> / <c>tools/call</c> request.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, McpToolCatalog> ToolCatalogCache = new();

    /// <summary>
    /// Per-controller-type cache of the <see cref="McpServerAttribute.Version"/>
    /// string. Same pattern as <see cref="ServerNameCache"/>.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, string> ServerVersionCache = new();

    /// <summary>
    /// JSON serializer options used by the JSON-RPC dispatcher
    /// (<see cref="DispatchToolsCall"/>) when deserializing <c>tools/call</c>
    /// arguments into a tool's input record and when serializing the result
    /// back into the <c>text</c> content block.
    ///
    /// <para><b>Why these aren't <c>Program.cs</c>'s MVC JSON options.</b>
    /// The REST surface (<c>POST /api/mcp/{server}/{ver}/{toolName}</c>) flows
    /// through MVC's model binder, which picks up the global
    /// <see cref="JsonSerializerOptions"/> configured in <c>Program.cs</c>
    /// (camelCase + <see cref="JsonStringEnumConverter"/> + ignore-cycles +
    /// ignore-null). The JSON-RPC path skips MVC binding for the inner
    /// <c>arguments</c> object — it deserializes manually from a
    /// <see cref="JsonElement"/> here — so the global options don't apply.
    /// We mirror the relevant subset (camelCase, ignore-null, string enums)
    /// to keep the two transports behaviourally identical. <b>If you change
    /// the global MVC options for a reason that affects MCP payloads, mirror
    /// the change here too.</b></para>
    /// </summary>
    private static readonly JsonSerializerOptions JsonRpcSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            // Without this, int-backed enums on tool input records
            // (e.g. ProjectKanbanCardStatus on CreateCardInput) reject
            // their string names ("Backlog", "Todo", …) with a JsonException
            // that the dispatcher surfaces as JSON-RPC invalid_params. Every
            // model-driven MCP call sends enum-as-string, so this converter
            // is load-bearing.
            new JsonStringEnumConverter(),
        },
    };

    /// <summary>
    /// Field names a client must never supply — those are server-derived from the
    /// JWT claim. Case-insensitive match. We strip rather than reject because the
    /// daemon's MCP client may forward an upstream agent's payload that happens to
    /// include one of these by accident; rejecting would be hostile UX. The strip
    /// emits a structured warning so abuse is still observable.
    /// </summary>
    private static readonly HashSet<string> ForbiddenFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "projectId",
        "tenantId",
        "runtimeId",
    };

    protected ApplicationDbContext Db { get; }
    protected ILogger<McpControllerBase> Logger { get; }
    protected McpRateLimiter RateLimiter { get; }

    private Guid? _runtimeId;
    private Guid? _projectId;
    private string? _serverName;

    protected McpControllerBase(
        ApplicationDbContext db,
        ILogger<McpControllerBase> logger,
        McpRateLimiter rateLimiter)
    {
        Db = db;
        Logger = logger;
        RateLimiter = rateLimiter;
    }

    /// <summary>Runtime id resolved from the <c>rt_runtime</c> JWT claim.</summary>
    protected Guid RuntimeId
    {
        get
        {
            ResolveClaims();
            return _runtimeId!.Value;
        }
    }

    /// <summary>Project id resolved from the <c>rt_project</c> JWT claim.</summary>
    protected Guid ProjectId
    {
        get
        {
            ResolveClaims();
            return _projectId!.Value;
        }
    }

    /// <summary>MCP server name from the <see cref="McpServerAttribute"/> on the concrete controller.</summary>
    protected string ServerName
    {
        get
        {
            _serverName ??= ResolveServerName(GetType());
            return _serverName;
        }
    }

    /// <summary>
    /// Wraps a concrete MCP method handler with the framework's authentication
    /// cross-check, forbidden-field strip, audit-row write, and uniform envelope.
    /// Sub-classes should generally call this from every action and let the base
    /// class own the boilerplate.
    ///
    /// <para>Always returns HTTP 200 with an <see cref="McpResponse{T}"/> body —
    /// failures are encoded inside the envelope, never as 4xx. The MCP convention
    /// reserves HTTP-level errors for transport / auth concerns (handled by the
    /// JWT bearer middleware before this action runs).</para>
    ///
    /// <para><b>Rate limiting</b> (spec 15 Card 6). Immediately after the claim
    /// resolution and before the forbidden-field strip, the framework consults
    /// <see cref="RateLimiter"/> on a <c>(runtimeId, serverName, method)</c> key.
    /// A denied call returns the envelope with <c>error.code = "rate_limit_exceeded"</c>,
    /// a <c>retryAfterMs</c> hint in <c>error.details</c>, and audit
    /// <see cref="McpCall.Status"/> = <see cref="McpCallStatus.RateLimited"/>.
    /// The handler is not called.</para>
    /// </summary>
    protected async Task<IActionResult> InvokeAsync<TIn, TOut>(
        string method,
        TIn? input,
        Func<TIn?, Task<Result<TOut>>> handler,
        CancellationToken ct)
    {
        // Resolve claims + server name eagerly. Both are cheap (cached) and we
        // want any malformed-principal failure to surface before we start the
        // stopwatch / serialise input.
        ResolveClaims();
        _ = ServerName; // forces reflection cache read

        // Rate limit gate. Consulted before the forbidden-field strip + handler
        // dispatch — denied calls don't run any handler-side logic. The
        // per-method budget is read from [McpMethodRateLimit] on the action
        // (cached); absence ⇒ framework defaults.
        var (capacity, refillPerSecond) = ResolveRateLimit(method);
        var decision = RateLimiter.TryAcquire(RuntimeId, ServerName, method, capacity, refillPerSecond);
        if (!decision.Allowed)
        {
            // Audit row first (defense in depth) — same swallow-on-failure
            // policy as the success path; losing the audit must not corrupt
            // the response.
            try
            {
                Db.McpCalls.Add(new McpCall
                {
                    Id = Guid.NewGuid(),
                    RuntimeId = RuntimeId,
                    ServerName = ServerName,
                    Method = method,
                    DurationMs = 0,
                    Status = McpCallStatus.RateLimited,
                    ErrorCode = "rate_limit_exceeded",
                    RequestSizeBytes = 0,
                    ResponseSizeBytes = 0,
                    CreatedAt = DateTime.UtcNow,
                });
                await Db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    ex,
                    "Failed to write rate-limited McpCall audit row for {Server}.{Method} (runtime {RuntimeId})",
                    ServerName, method, RuntimeId);
            }

            var rateLimitEnvelope = new McpResponse<TOut>(default, new McpError(
                Code: "rate_limit_exceeded",
                Message: "Rate limit exceeded",
                Retryable: true,
                Details: new Dictionary<string, object>
                {
                    ["retryAfterMs"] = decision.RetryAfterMs,
                }));
            return Ok(rateLimitEnvelope);
        }

        // Forbidden-field strip — see ForbiddenFieldNames. Reflection result is
        // cached per TIn after the first call.
        StripForbiddenFields(input);

        var stopwatch = Stopwatch.StartNew();
        var requestSizeBytes = EstimateSerializedSize(input);

        TOut? output = default;
        var status = McpCallStatus.Success;
        string? errorCode = null;
        McpError? error = null;

        try
        {
            var result = await handler(input);
            if (result.IsSuccess)
            {
                output = result.Value;
            }
            else
            {
                // Result.Failure — caller supplied bad input or hit an
                // application-level problem the handler chose to model as a
                // failure rather than an exception. Bucket as ClientError; a
                // future card can refine via subclass override if a handler
                // genuinely needs to express ServerError without throwing.
                status = McpCallStatus.ClientError;
                errorCode = result.Error;
                error = new McpError(
                    Code: result.Error ?? "unknown_error",
                    Message: result.Error ?? "Unknown error",
                    Retryable: false,
                    Details: null);
            }
        }
        catch (Exception ex)
        {
            // Unhandled — log the exception with structured context, but never
            // surface the inner message to the caller (it may carry
            // environmentals / stack traces). The envelope returns a generic
            // "internal_error" code.
            Logger.LogError(
                ex,
                "MCP handler {Server}.{Method} threw unhandled exception (runtime {RuntimeId}, project {ProjectId})",
                ServerName, method, RuntimeId, ProjectId);
            status = McpCallStatus.ServerError;
            errorCode = "internal_error";
            error = new McpError(
                Code: "internal_error",
                Message: "Internal server error",
                Retryable: false,
                Details: null);
        }

        var envelope = new McpResponse<TOut>(output, error);
        var responseSizeBytes = EstimateSerializedSize(envelope);

        stopwatch.Stop();

        // Write the audit row. If this throws (e.g. DB transient failure), we
        // log and swallow — losing the audit row is bad, but failing the actual
        // MCP response because audit failed would be worse: the daemon would
        // think the call didn't happen and likely retry, which compounds the
        // problem. Ops will see the log; the structured response still returns.
        try
        {
            Db.McpCalls.Add(new McpCall
            {
                Id = Guid.NewGuid(),
                RuntimeId = RuntimeId,
                ServerName = ServerName,
                Method = method,
                DurationMs = (int)stopwatch.ElapsedMilliseconds,
                Status = status,
                ErrorCode = errorCode,
                RequestSizeBytes = requestSizeBytes,
                ResponseSizeBytes = responseSizeBytes,
                CreatedAt = DateTime.UtcNow,
            });
            await Db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex,
                "Failed to write McpCall audit row for {Server}.{Method} (runtime {RuntimeId})",
                ServerName, method, RuntimeId);
        }

        return Ok(envelope);
    }

    /// <summary>
    /// Populates <see cref="_runtimeId"/> and <see cref="_projectId"/> from the
    /// JWT claims on <see cref="ControllerBase.User"/>. Idempotent — second and
    /// later calls are a no-op. Throws <see cref="InvalidOperationException"/>
    /// if either claim is missing or malformed; in production this would be a
    /// 401 from the JWT bearer middleware before reaching the controller, so
    /// landing here is a sign of a misconfigured auth pipeline rather than an
    /// expected error path.
    /// </summary>
    private void ResolveClaims()
    {
        if (_runtimeId.HasValue && _projectId.HasValue) return;

        var runtimeStr = User.FindFirstValue(RuntimeTokenClaimNames.RuntimeId);
        if (!Guid.TryParse(runtimeStr, out var runtimeId))
        {
            throw new InvalidOperationException(
                $"MCP request reached {GetType().Name} without a valid '{RuntimeTokenClaimNames.RuntimeId}' claim — auth middleware should have rejected this.");
        }

        var projectStr = User.FindFirstValue(RuntimeTokenClaimNames.ProjectId);
        if (!Guid.TryParse(projectStr, out var projectId))
        {
            throw new InvalidOperationException(
                $"MCP request reached {GetType().Name} without a valid '{RuntimeTokenClaimNames.ProjectId}' claim — auth middleware should have rejected this.");
        }

        _runtimeId = runtimeId;
        _projectId = projectId;
    }

    /// <summary>
    /// Resolve the MCP server name from the concrete controller's
    /// <see cref="McpServerAttribute"/>. Cached per-type. Throws
    /// <see cref="InvalidOperationException"/> if the attribute is missing —
    /// this is a wire-up error and should fail loudly on first call.
    /// </summary>
    private static string ResolveServerName(Type controllerType) =>
        ServerNameCache.GetOrAdd(controllerType, static t =>
        {
            var attr = t.GetCustomAttribute<McpServerAttribute>(inherit: false);
            if (attr is null)
            {
                throw new InvalidOperationException(
                    $"MCP controller {t.FullName} is missing [McpServer(name, version)] — every concrete McpControllerBase must declare its server name.");
            }
            return attr.Name;
        });

    /// <summary>
    /// Zero out any client-supplied <c>projectId</c> / <c>tenantId</c> /
    /// <c>runtimeId</c> property on <paramref name="input"/>. Reflection result
    /// is cached per <typeparamref name="TIn"/>. Per spec, the contract is
    /// "ignore-with-warning", not "reject" — we log a structured warning the
    /// first time we see one of these and proceed with the field set to its
    /// type default.
    /// </summary>
    private void StripForbiddenFields<TIn>(TIn? input)
    {
        if (input is null) return;

        var type = input.GetType();
        // Skip primitives / strings — they don't have properties of interest.
        if (type.IsPrimitive || type == typeof(string)) return;

        var forbidden = ForbiddenFieldsCache.GetOrAdd(type, static t =>
            t.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.CanRead && p.CanWrite && ForbiddenFieldNames.Contains(p.Name))
                .ToArray());

        if (forbidden.Length == 0) return;

        foreach (var prop in forbidden)
        {
            var current = prop.GetValue(input);
            if (current is null) continue;

            // Only warn if the field actually held a value — clients passing
            // null (the default) is the expected case and shouldn't generate
            // log noise.
            Logger.LogWarning(
                "MCP {Server}: client-supplied scope field '{Field}' ignored — server-side claim takes precedence (runtime {RuntimeId}, project {ProjectId})",
                ServerName, prop.Name, _runtimeId, _projectId);

            var defaultValue = prop.PropertyType.IsValueType
                ? Activator.CreateInstance(prop.PropertyType)
                : null;
            prop.SetValue(input, defaultValue);
        }
    }

    /// <summary>
    /// Resolve the rate-limit budget for the current MCP method. Reads
    /// <see cref="McpMethodRateLimitAttribute"/> from the action's
    /// <see cref="MethodInfo"/> via <see cref="ControllerActionDescriptor"/> and
    /// caches the result per <c>(controllerType, mcpMethodName)</c> pair.
    /// Falls back to <see cref="McpRateLimiter.DefaultCapacity"/> /
    /// <see cref="McpRateLimiter.DefaultRefillPerSecond"/> when the attribute
    /// is absent or the action descriptor isn't available (e.g. unit tests
    /// that drive the controller without going through MVC routing).
    /// </summary>
    private (int Capacity, double RefillPerSecond) ResolveRateLimit(string mcpMethodName)
    {
        var controllerType = GetType();
        return RateLimitCache.GetOrAdd((controllerType, mcpMethodName), key =>
        {
            // ControllerContext.ActionDescriptor is null in pure unit-test
            // harnesses — fall back to defaults rather than throwing. The
            // controller is still usable in tests; production always populates
            // the descriptor.
            var actionDescriptor = ControllerContext?.ActionDescriptor as ControllerActionDescriptor;
            var methodInfo = actionDescriptor?.MethodInfo;
            var attr = methodInfo?.GetCustomAttribute<McpMethodRateLimitAttribute>(inherit: false);
            if (attr is null)
            {
                return (McpRateLimiter.DefaultCapacity, McpRateLimiter.DefaultRefillPerSecond);
            }
            return (attr.Capacity, attr.RefillPerSecond);
        });
    }

    // ========================================================================
    // JSON-RPC / MCP Streamable HTTP dispatcher (card 1 of mcp-streamable-http-
    // transport spec). The daemon MCP client POSTs `{"jsonrpc":"2.0",...}` at the
    // controller's base route. We dispatch initialize / tools/list / tools/call
    // here and delegate tools/call back to the existing [HttpPost("name")]
    // actions on the concrete controller — so every existing security, audit,
    // rate-limit, and force-scope guarantee in InvokeAsync<TIn,TOut> applies
    // unchanged. The REST routes still exist (deprecate in card-4); this is
    // purely additive.
    // ========================================================================

    /// <summary>
    /// JSON-RPC 2.0 / MCP Streamable HTTP entry point. Mounted at the
    /// controller's base route (e.g. <c>POST /api/mcp/kanban/v1</c>) so the Claude
    /// SDK's `mcpServers[name].url` config resolves correctly without further
    /// per-server wire-up.
    ///
    /// <para>Always returns HTTP 200 with a JSON-RPC envelope — HTTP-level
    /// errors are reserved for transport / auth (handled by the JWT bearer
    /// middleware before this action runs).</para>
    /// </summary>
    [HttpPost("")]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<IActionResult> HandleJsonRpc(
        [FromBody] McpJsonRpcRequest? request,
        CancellationToken ct)
    {
        if (request is null || string.IsNullOrEmpty(request.JsonRpc) || string.IsNullOrEmpty(request.Method))
        {
            return JsonRpcError(null, McpJsonRpcConstants.InvalidRequest, "Invalid JSON-RPC request");
        }

        // Resolve auth claims + server name up front — both are cached, both
        // surface configuration errors loudly. ResolveClaims throws if the
        // RuntimeToken claim is missing, which would be a 401 from middleware
        // in practice; the safety-net try/catch below maps it to an internal
        // error so a wire request never crashes the action.
        try
        {
            ResolveClaims();
            _ = ServerName;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "JSON-RPC dispatch failed before method routing on {Server}", GetType().Name);
            return JsonRpcError(request.Id, McpJsonRpcConstants.InternalError, "Internal error");
        }

        return request.Method switch
        {
            "initialize"
                => JsonRpcSuccess(request.Id, BuildInitializeResult()),
            "notifications/initialized" or "initialized"
                // Per spec the initialized notification has no response, but
                // SDK clients tolerate a benign success. Keep it simple.
                => JsonRpcSuccess(request.Id, new { }),
            "tools/list"
                => JsonRpcSuccess(request.Id, BuildToolsList()),
            "tools/call"
                => await DispatchToolsCall(request.Id, request.Params, ct),
            _
                => JsonRpcError(request.Id, McpJsonRpcConstants.MethodNotFound,
                                $"Method not found: {request.Method}"),
        };
    }

    private object BuildInitializeResult()
    {
        var version = ServerVersionCache.GetOrAdd(GetType(), static t =>
            t.GetCustomAttribute<McpServerAttribute>(inherit: false)?.Version ?? "v1");
        return new McpInitializeResult(
            ProtocolVersion: McpJsonRpcConstants.ProtocolVersion,
            ServerInfo: new McpServerInfo(Name: ServerName, Version: version),
            Capabilities: new McpCapabilities(Tools: new McpToolsCapability(ListChanged: false)));
    }

    private object BuildToolsList()
    {
        var catalog = ResolveToolCatalog(GetType());
        var tools = catalog.Tools
            .Select(t => new McpToolDescriptor(t.Name, t.Description, t.InputSchema))
            .ToList();
        return new McpToolsListResult(tools);
    }

    /// <summary>
    /// Route a <c>tools/call</c> to the concrete controller's existing
    /// <c>[HttpPost("name")]</c> action by invoking it via reflection. The
    /// action still goes through <see cref="InvokeAsync{TIn,TOut}"/>, so
    /// claims, forbidden-field strip, audit, and rate-limit all fire
    /// identically to a REST call. We unwrap the <see cref="McpResponse{T}"/>
    /// envelope returned by the action and map it to the JSON-RPC
    /// <c>result</c> / <c>error</c> shape.
    /// </summary>
    private async Task<IActionResult> DispatchToolsCall(
        JsonElement? rpcId, JsonElement? paramsEl, CancellationToken ct)
    {
        if (paramsEl is null || paramsEl.Value.ValueKind != JsonValueKind.Object)
        {
            return JsonRpcError(rpcId, McpJsonRpcConstants.InvalidParams,
                "Missing tools/call params");
        }

        if (!paramsEl.Value.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
        {
            return JsonRpcError(rpcId, McpJsonRpcConstants.InvalidParams,
                "Missing or invalid 'name' in tools/call params");
        }
        var toolName = nameEl.GetString()!;

        var catalog = ResolveToolCatalog(GetType());
        if (!catalog.ToolsByName.TryGetValue(toolName, out var tool))
        {
            return JsonRpcError(rpcId, McpJsonRpcConstants.InvalidParams,
                $"Unknown tool: {toolName}");
        }

        // Deserialize arguments into the action's input type. Methods whose
        // input type is null (e.g. getKanbanBoard with no body) ignore this.
        object? input = null;
        if (tool.InputType is not null
            && paramsEl.Value.TryGetProperty("arguments", out var argsEl)
            && argsEl.ValueKind == JsonValueKind.Object)
        {
            try
            {
                input = argsEl.Deserialize(tool.InputType, JsonRpcSerializerOptions);
            }
            catch (JsonException ex)
            {
                return JsonRpcError(rpcId, McpJsonRpcConstants.InvalidParams,
                    $"Failed to parse arguments for {toolName}: {ex.Message}");
            }
        }

        // Build the parameter list for the existing action. Every existing
        // MCP action has the signature `(TIn? input, CancellationToken ct)`
        // or `(CancellationToken ct)` for body-less methods. Resolve by name.
        var paramInfos = tool.Method.GetParameters();
        var args = new object?[paramInfos.Length];
        for (var i = 0; i < paramInfos.Length; i++)
        {
            var p = paramInfos[i];
            if (p.ParameterType == typeof(CancellationToken))
            {
                args[i] = ct;
            }
            else if (tool.InputType is not null && p.ParameterType == tool.InputType)
            {
                args[i] = input;
            }
            else
            {
                // Defensive: unknown parameter shape. Pass the default and let
                // the action's null-check (every existing action has one)
                // emit `invalid_input`.
                args[i] = p.ParameterType.IsValueType
                    ? Activator.CreateInstance(p.ParameterType)
                    : null;
            }
        }

        // Invoke. The action returns Task<IActionResult>; await it and unwrap.
        object? invokeResult;
        try
        {
            invokeResult = tool.Method.Invoke(this, args);
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            Logger.LogError(tie.InnerException,
                "MCP tool {Server}.{Tool} threw while dispatching", ServerName, toolName);
            return JsonRpcError(rpcId, McpJsonRpcConstants.InternalError, "Internal error");
        }

        if (invokeResult is not Task<IActionResult> actionTask)
        {
            Logger.LogError(
                "MCP tool {Server}.{Tool} returned non-Task<IActionResult> {Type} — wire-up bug",
                ServerName, toolName, invokeResult?.GetType().FullName);
            return JsonRpcError(rpcId, McpJsonRpcConstants.InternalError, "Internal error");
        }

        var actionResult = await actionTask;

        // Existing actions always return Ok(McpResponse<T>) via InvokeAsync.
        if (actionResult is not ObjectResult okResult || okResult.Value is null)
        {
            Logger.LogError(
                "MCP tool {Server}.{Tool} did not return an Ok envelope (got {Type})",
                ServerName, toolName, actionResult.GetType().Name);
            return JsonRpcError(rpcId, McpJsonRpcConstants.InternalError, "Internal error");
        }

        // Reflect over the McpResponse<T> envelope. We don't know T statically
        // at dispatch time; reading `Result` + `Error` by property name keeps
        // this branch generic.
        var envelope = okResult.Value;
        var envelopeType = envelope.GetType();
        var errorVal = envelopeType.GetProperty("Error")?.GetValue(envelope) as McpError;
        var resultVal = envelopeType.GetProperty("Result")?.GetValue(envelope);

        if (errorVal is not null)
        {
            // Surface the envelope failure as a JSON-RPC error but preserve
            // the full McpError in `data` so the SDK / model can see code +
            // retryable. We keep HTTP 200 per JSON-RPC convention.
            return JsonRpcError(rpcId, McpJsonRpcConstants.McpEnvelopeError,
                errorVal.Message, data: errorVal);
        }

        // Success: emit a single text content block carrying the JSON
        // representation of the result. This is the standard MCP tools/call
        // result shape; the model parses the text back into structured data
        // when it needs to act on the response.
        var text = JsonSerializer.Serialize(resultVal, JsonRpcSerializerOptions);
        var callResult = new McpToolsCallResult(
            Content: new[] { new McpContentBlock(Type: "text", Text: text) },
            IsError: false);
        return JsonRpcSuccess(rpcId, callResult);
    }

    private IActionResult JsonRpcSuccess(JsonElement? id, object result) =>
        Ok(new McpJsonRpcSuccessResponse(McpJsonRpcConstants.JsonRpcVersion, id, result));

    private IActionResult JsonRpcError(JsonElement? id, int code, string message, object? data = null) =>
        Ok(new McpJsonRpcErrorResponse(
            McpJsonRpcConstants.JsonRpcVersion, id,
            new McpJsonRpcError(code, message, data)));

    /// <summary>
    /// Build (or fetch from cache) the JSON-RPC tool catalog for a concrete
    /// controller type. Each <c>[HttpPost("toolName")]</c> action becomes a
    /// tool whose <c>inputSchema</c> is reflected from its <c>[FromBody]</c>
    /// parameter type. The description comes from <c>[Description]</c> on
    /// the action if present, else the action's name. The dispatcher itself
    /// (this <see cref="HandleJsonRpc"/> method, route pattern <c>""</c>) is
    /// excluded — only named routes are tools.
    /// </summary>
    private static McpToolCatalog ResolveToolCatalog(Type controllerType) =>
        ToolCatalogCache.GetOrAdd(controllerType, static t =>
        {
            var tools = new Dictionary<string, McpToolEntry>(StringComparer.Ordinal);
            var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            foreach (var method in methods)
            {
                var httpPost = method.GetCustomAttribute<HttpPostAttribute>();
                if (httpPost is null) continue;
                var route = httpPost.Template;
                // Skip the JSON-RPC entry point itself (route pattern "" on the
                // base class) and any other unnamed routes. The dispatcher only
                // exposes named [HttpPost("toolName")] siblings.
                if (string.IsNullOrEmpty(route)) continue;

                // Resolve the input type from the first non-CancellationToken
                // parameter. Every existing MCP action follows the convention
                // `(TIn? input, CancellationToken ct)`; body-less methods use
                // `(CancellationToken ct)` only.
                Type? inputType = null;
                foreach (var p in method.GetParameters())
                {
                    if (p.ParameterType == typeof(CancellationToken)) continue;
                    inputType = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
                    break;
                }

                var schema = McpJsonSchemaGenerator.BuildSchema(inputType);

                // Description: the action's XML doc isn't reachable at runtime
                // without a doc-xml file; settle for the action name as a
                // fallback. A future card can either thread an attribute or
                // ship the doc-xml alongside the assembly.
                var description = method.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description
                                  ?? route;

                tools[route] = new McpToolEntry(
                    Name: route,
                    Description: description,
                    InputType: inputType,
                    Method: method,
                    InputSchema: schema);
            }
            return new McpToolCatalog(tools);
        });

    /// <summary>
    /// Estimate the serialized byte size of <paramref name="value"/> for the
    /// audit row's request/response size columns. We accept the small CPU cost
    /// of a JSON serialise here — the alternative (emitting a placeholder of 0)
    /// would lose a useful capacity-planning signal. <c>null</c> returns 0.
    /// </summary>
    private static int EstimateSerializedSize<T>(T value)
    {
        if (value is null) return 0;
        try
        {
            return JsonSerializer.SerializeToUtf8Bytes(value).Length;
        }
        catch
        {
            // A non-serializable value (e.g. cycle) shouldn't fail the call —
            // record 0 and move on. Vanishingly rare in practice.
            return 0;
        }
    }
}

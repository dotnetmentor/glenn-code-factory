using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Source.Features.SignalR.Hubs;
using Source.Features.SignalR.Services;
using Source.Infrastructure;
using Source.Infrastructure.Extensions;

namespace Source.Features.Diffs.Controllers;

/// <summary>
/// User-facing HTTP surface for the Changes tab — Phase 1 of
/// diff-view-tab-spec. Two read endpoints, both proxy to the project's
/// connected daemon via the <see cref="RuntimeHub"/> hub group, then return
/// the daemon's response verbatim.
///
/// <para><b>Why no MediatR.</b> Same pragmatic reasoning as
/// <see cref="Source.Features.GitOps.Controllers.GitBranchesController"/>:
/// thin runtime-resolution + a single hub round-trip, no cross-slice events.
/// Wrapping in commands/handlers would add four files without changing
/// behaviour.</para>
///
/// <para><b>Phase scoping.</b> Only <c>scope=workingTree</c> is implemented;
/// branch / commit / range scopes return 400 with a clear message. The wire
/// shape (<see cref="ChangedFilesResponse"/>, <see cref="FileDiffResponse"/>)
/// already accommodates the future scopes — the picker UI in Phase 3 wires
/// them up without a contract change.</para>
///
/// <para><b>Authorisation.</b> Default JWT bearer (cookie-backed user
/// session) plus per-project ownership gating: the runtime is loaded with
/// its <see cref="Source.Features.Projects.Models.Project"/> nav and the
/// caller's <c>NameIdentifier</c> claim must match
/// <c>Project.OwnerUserId</c>. A mismatch returns <c>404</c> (NOT 403) so
/// we don't leak runtime-id existence cross-tenant — same convention the
/// existing TODOs on <see cref="Source.Features.Conversations.Controllers.SessionsController"/>
/// and <see cref="Source.Features.Conversations.Controllers.QueueController"/>
/// describe. The daemon never calls these endpoints; it's the user clicking
/// in the Changes tab.</para>
/// </summary>
[ApiController]
[Route("api/runtimes/{runtimeId:guid}/diff")]
[Authorize]
[Tags("Diffs")]
public class DiffsController : ControllerBase
{
    private const string WorkingTreeScope = "workingTree";
    private const string BranchScope = "branch";

    /// <summary>
    /// Default head ref when the caller doesn't pin one. The branch-scope
    /// default UX is "compare against main"; the daemon resolves
    /// <c>HEAD</c> to whichever commit the runtime's working tree is on.
    /// </summary>
    private const string DefaultHeadRef = "HEAD";

    /// <summary>
    /// Cap for the commit-picker row count surfaced to a single
    /// <c>GET /commits</c> call. Mirrors the daemon's own hard cap (1000)
    /// so a misconfigured frontend can't pull more rows than the daemon
    /// will ship anyway. The default (200) lives on the daemon side.
    /// </summary>
    private const int MaxCommitLimit = 1000;

    /// <summary>
    /// Bound to the daemon side via the .NET strongly-typed proxy generator;
    /// any rename on <see cref="IRuntimeClient.GetChangedFiles"/> /
    /// <see cref="IRuntimeClient.GetFileDiff"/> breaks compile here, which is
    /// the whole point of using TypedSignalR for the daemon contract.
    /// </summary>
    private const int DaemonInvokeTimeoutSeconds = 30;

    private readonly ApplicationDbContext _db;
    private readonly IHubContext<RuntimeHub, IRuntimeClient> _runtimeHub;
    private readonly IRuntimeConnectionRegistry _connections;
    private readonly ILogger<DiffsController> _logger;

    public DiffsController(
        ApplicationDbContext db,
        IHubContext<RuntimeHub, IRuntimeClient> runtimeHub,
        IRuntimeConnectionRegistry connections,
        ILogger<DiffsController> logger)
    {
        _db = db;
        _runtimeHub = runtimeHub;
        _connections = connections;
        _logger = logger;
    }

    /// <summary>
    /// Return the list of files that differ in the requested scope. Supports
    /// two scopes today:
    ///
    /// <list type="bullet">
    ///   <item><c>workingTree</c> — HEAD vs the working tree on disk
    ///   (untracked files included). Auto-commit makes this scope mostly
    ///   empty in normal use.</item>
    ///   <item><c>branch</c> — diff between two refs
    ///   (<paramref name="base"/>..<paramref name="head"/>). The new default
    ///   UX picks <c>main</c> as base and <c>HEAD</c> as head so users see
    ///   "everything I've changed on this branch" at a glance.</item>
    /// </list>
    ///
    /// <para>404 when no runtime exists for the runtimeId. 503 when the
    /// daemon for that runtime is not currently connected (the user can
    /// retry once it reconnects). 400 when <c>scope</c> is unrecognised, or
    /// when <c>scope=branch</c> and <c>base</c> is missing / unresolvable.</para>
    /// </summary>
    [HttpGet("changed-files")]
    [ProducesResponseType(typeof(ChangedFilesResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<ChangedFilesResponse>> GetChangedFiles(
        Guid runtimeId,
        [FromQuery] string scope,
        [FromQuery] string? @base,
        [FromQuery] string? head,
        CancellationToken ct)
    {
        // Scope validation up-front so we don't burn a runtime / connection
        // lookup on an obviously-malformed request.
        if (!string.Equals(scope, WorkingTreeScope, StringComparison.Ordinal) &&
            !string.Equals(scope, BranchScope, StringComparison.Ordinal))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Unknown scope",
                Detail = $"scope '{scope}' is not supported. Use 'workingTree' or 'branch'.",
                Status = 400,
            });
        }

        if (string.Equals(scope, BranchScope, StringComparison.Ordinal) && string.IsNullOrWhiteSpace(@base))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Missing base ref",
                Detail = "scope=branch requires a non-empty 'base' query parameter (typically the default branch, e.g. 'main').",
                Status = 400,
            });
        }

        var runtime = await _db.ResolveOwnedRuntimeAsync(User, runtimeId, ct);
        if (runtime is null)
        {
            return NotFound();
        }

        // Typed-client methods that return a value compile down to InvokeAsync,
        // which ASP.NET Core SignalR only supports against a single client
        // connection — never a Group / All target (it throws
        // "InvokeAsync only works with Single clients"). The runtime → daemon
        // connectionId map is populated by TrackRuntimeConnectionHandler off
        // the standard RuntimeConnected/RuntimeDisconnected events; a missing
        // entry means the daemon is not currently connected, which we surface
        // as 503 (same UX as the IOException path below).
        var connectionId = _connections.TryGet(runtime.Id);
        if (connectionId is null)
        {
            _logger.LogWarning(
                "DiffsController.GetChangedFiles: no daemon connection registered for runtime {RuntimeId}; returning 503.",
                runtime.Id);
            return StatusCode(503, new { error = "daemon offline — try again once the runtime reconnects" });
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(DaemonInvokeTimeoutSeconds));

            ChangedFilesResponse response;
            if (string.Equals(scope, BranchScope, StringComparison.Ordinal))
            {
                // Both refs are already validated as non-empty (base) /
                // defaulted (head). The daemon validates that they actually
                // resolve via `git rev-parse --verify` and throws a typed
                // error we catch below as a 400.
                var headRef = string.IsNullOrWhiteSpace(head) ? DefaultHeadRef : head;
                response = await _runtimeHub.Clients
                    .Client(connectionId)
                    .GetBranchChangedFiles(runtime.Id.ToString(), @base!, headRef)
                    .WaitAsync(timeout.Token);
            }
            else
            {
                // Working-tree scope: base / head are unused on the wire (the
                // daemon ignores them) but the request DTO carries them so a
                // future scope add doesn't break compatibility.
                var request = new ChangedFilesRequest(scope, @base, head);
                response = await _runtimeHub.Clients
                    .Client(connectionId)
                    .GetChangedFiles(runtime.Id.ToString(), request)
                    .WaitAsync(timeout.Token);
            }

            return Ok(response);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "DiffsController.GetChangedFiles: daemon for runtime {RuntimeId} did not respond within {TimeoutSeconds}s.",
                runtime.Id, DaemonInvokeTimeoutSeconds);
            return StatusCode(504, new { error = "daemon did not respond in time" });
        }
        catch (IOException ex)
        {
            // SignalR's underlying transport raises IOException when the
            // daemon's connection has dropped or no client is in the group.
            _logger.LogWarning(ex,
                "DiffsController.GetChangedFiles: daemon for runtime {RuntimeId} unreachable; the user can retry once it reconnects.",
                runtime.Id);
            return StatusCode(503, new { error = "daemon offline — try again once the runtime reconnects" });
        }
        catch (HubException ex) when (IsInvalidRefError(ex))
        {
            // Daemon-side validation says base / head can't be resolved.
            // Surface as 400 so the user gets a readable error instead of a
            // 500. Branch-name typo is the common case.
            _logger.LogInformation(
                "DiffsController.GetChangedFiles: daemon reported invalid ref for runtime {RuntimeId} (base={Base}, head={Head}).",
                runtime.Id, @base, head);
            return BadRequest(new ProblemDetails
            {
                Title = "Unknown git ref",
                Detail = ex.Message,
                Status = 400,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "DiffsController.GetChangedFiles: unexpected failure for runtime {RuntimeId}.",
                runtime.Id);
            return StatusCode(500, new { error = "unexpected diff query failure" });
        }
    }

    /// <summary>
    /// Return the unified-diff text for a single file in the requested scope.
    /// Supports <c>workingTree</c> (HEAD vs working tree) and <c>branch</c>
    /// (<c>base..head</c>). The <c>path</c> query parameter is the
    /// post-rename (head) path the row's <c>Path</c> field carried in the
    /// changed-files response.
    ///
    /// <para>404 when no runtime exists. 503 when the daemon is not
    /// connected. 400 for unsupported scopes, missing path, missing base on
    /// the branch scope, or unresolvable refs.</para>
    /// </summary>
    [HttpGet("file")]
    [ProducesResponseType(typeof(FileDiffResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<FileDiffResponse>> GetFileDiff(
        Guid runtimeId,
        [FromQuery] string scope,
        [FromQuery] string path,
        [FromQuery] string? @base,
        [FromQuery] string? head,
        CancellationToken ct)
    {
        if (!string.Equals(scope, WorkingTreeScope, StringComparison.Ordinal) &&
            !string.Equals(scope, BranchScope, StringComparison.Ordinal))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Unknown scope",
                Detail = $"scope '{scope}' is not supported. Use 'workingTree' or 'branch'.",
                Status = 400,
            });
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Missing path",
                Detail = "path query parameter is required",
                Status = 400,
            });
        }

        if (string.Equals(scope, BranchScope, StringComparison.Ordinal) && string.IsNullOrWhiteSpace(@base))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Missing base ref",
                Detail = "scope=branch requires a non-empty 'base' query parameter.",
                Status = 400,
            });
        }

        var runtime = await _db.ResolveOwnedRuntimeAsync(User, runtimeId, ct);
        if (runtime is null)
        {
            return NotFound();
        }

        // See GetChangedFiles for why we resolve a single connectionId rather
        // than fanning out via the runtime-{id} group: typed-client methods
        // with a return value invoke through InvokeAsync, which only works on
        // a single client connection.
        var connectionId = _connections.TryGet(runtime.Id);
        if (connectionId is null)
        {
            _logger.LogWarning(
                "DiffsController.GetFileDiff: no daemon connection registered for runtime {RuntimeId} (path={Path}); returning 503.",
                runtime.Id, path);
            return StatusCode(503, new { error = "daemon offline — try again once the runtime reconnects" });
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(DaemonInvokeTimeoutSeconds));

            FileDiffResponse response;
            if (string.Equals(scope, BranchScope, StringComparison.Ordinal))
            {
                var headRef = string.IsNullOrWhiteSpace(head) ? DefaultHeadRef : head;
                response = await _runtimeHub.Clients
                    .Client(connectionId)
                    .GetBranchFileDiff(runtime.Id.ToString(), @base!, headRef, path)
                    .WaitAsync(timeout.Token);
            }
            else
            {
                var request = new FileDiffRequest(scope, @base, head, path);
                response = await _runtimeHub.Clients
                    .Client(connectionId)
                    .GetFileDiff(runtime.Id.ToString(), request)
                    .WaitAsync(timeout.Token);
            }

            return Ok(response);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "DiffsController.GetFileDiff: daemon for runtime {RuntimeId} did not respond within {TimeoutSeconds}s (path={Path}).",
                runtime.Id, DaemonInvokeTimeoutSeconds, path);
            return StatusCode(504, new { error = "daemon did not respond in time" });
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex,
                "DiffsController.GetFileDiff: daemon for runtime {RuntimeId} unreachable (path={Path}).",
                runtime.Id, path);
            return StatusCode(503, new { error = "daemon offline — try again once the runtime reconnects" });
        }
        catch (HubException ex) when (IsInvalidRefError(ex))
        {
            _logger.LogInformation(
                "DiffsController.GetFileDiff: daemon reported invalid ref for runtime {RuntimeId} (base={Base}, head={Head}, path={Path}).",
                runtime.Id, @base, head, path);
            return BadRequest(new ProblemDetails
            {
                Title = "Unknown git ref",
                Detail = ex.Message,
                Status = 400,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "DiffsController.GetFileDiff: unexpected failure for runtime {RuntimeId} (path={Path}).",
                runtime.Id, path);
            return StatusCode(500, new { error = "unexpected diff query failure" });
        }
    }

    /// <summary>
    /// List the commits reachable from <paramref name="head"/> but not
    /// <paramref name="base"/>, newest first. Drives the Phase 3
    /// commit-picker — the user picks any commit on the current branch as
    /// the base for the diff view. Same ownership / 404 / 503 / 400 model
    /// as the changed-files endpoint.
    ///
    /// <para>Default <paramref name="base"/> is none (must be supplied —
    /// returning every commit ever would be unsafe). Default
    /// <paramref name="head"/> is <c>HEAD</c>; default <paramref name="limit"/>
    /// is 200 (matches the daemon's own default). Cap is 1000.</para>
    /// </summary>
    [HttpGet("commits")]
    [ProducesResponseType(typeof(CommitRangeResponse), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<CommitRangeResponse>> GetCommitRange(
        Guid runtimeId,
        [FromQuery] string? @base,
        [FromQuery] string? head,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(@base))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Missing base ref",
                Detail = "'base' query parameter is required (typically the default branch, e.g. 'main').",
                Status = 400,
            });
        }

        var effectiveLimit = limit ?? 200;
        if (effectiveLimit <= 0 || effectiveLimit > MaxCommitLimit)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid limit",
                Detail = $"limit must be between 1 and {MaxCommitLimit}.",
                Status = 400,
            });
        }

        var runtime = await _db.ResolveOwnedRuntimeAsync(User, runtimeId, ct);
        if (runtime is null)
        {
            return NotFound();
        }

        var connectionId = _connections.TryGet(runtime.Id);
        if (connectionId is null)
        {
            _logger.LogWarning(
                "DiffsController.GetCommitRange: no daemon connection registered for runtime {RuntimeId}; returning 503.",
                runtime.Id);
            return StatusCode(503, new { error = "daemon offline — try again once the runtime reconnects" });
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(DaemonInvokeTimeoutSeconds));

            var headRef = string.IsNullOrWhiteSpace(head) ? DefaultHeadRef : head;
            var response = await _runtimeHub.Clients
                .Client(connectionId)
                .GetCommitRange(runtime.Id.ToString(), @base!, headRef, effectiveLimit)
                .WaitAsync(timeout.Token);

            return Ok(response);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "DiffsController.GetCommitRange: daemon for runtime {RuntimeId} did not respond within {TimeoutSeconds}s.",
                runtime.Id, DaemonInvokeTimeoutSeconds);
            return StatusCode(504, new { error = "daemon did not respond in time" });
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex,
                "DiffsController.GetCommitRange: daemon for runtime {RuntimeId} unreachable.",
                runtime.Id);
            return StatusCode(503, new { error = "daemon offline — try again once the runtime reconnects" });
        }
        catch (HubException ex) when (IsInvalidRefError(ex))
        {
            _logger.LogInformation(
                "DiffsController.GetCommitRange: daemon reported invalid ref for runtime {RuntimeId} (base={Base}, head={Head}).",
                runtime.Id, @base, head);
            return BadRequest(new ProblemDetails
            {
                Title = "Unknown git ref",
                Detail = ex.Message,
                Status = 400,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "DiffsController.GetCommitRange: unexpected failure for runtime {RuntimeId}.",
                runtime.Id);
            return StatusCode(500, new { error = "unexpected commit range query failure" });
        }
    }

    /// <summary>
    /// Detect the daemon-side <c>InvalidGitRefError</c> on the wire. SignalR
    /// marshals any thrown error as a <c>HubException</c> with the error
    /// message prefixed by <c>"InvalidGitRefError:"</c> (the error's name +
    /// message). We pattern-match on the prefix so unrelated hub exceptions
    /// don't get converted to 400s. The message format is stable because the
    /// daemon's <c>Error.name</c> property is set explicitly.
    /// </summary>
    private static bool IsInvalidRefError(HubException ex)
    {
        return ex.Message.Contains("InvalidGitRefError", StringComparison.Ordinal)
            || ex.Message.Contains("git ref not found", StringComparison.Ordinal);
    }

}

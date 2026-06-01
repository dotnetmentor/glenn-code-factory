using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Source.Features.Conversations.Models;
using Source.Features.RuntimeEvents.Models;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.RuntimeTokens.Models;
using Source.Infrastructure;
using Source.Infrastructure.Extensions;

namespace Source.Features.RuntimeLifecycle.Controllers;

/// <summary>
/// User-facing HTTP surface that returns the current lifecycle status of a project's
/// runtime — the data the project page header renders ("Online · arn · last heartbeat
/// 12s ago"). One endpoint, one query, one projection.
///
/// <para><b>Why no MediatR.</b> Same pragmatic reasoning as
/// <see cref="Source.Features.FlyManagement.Controllers.FlyAdminController"/> and
/// <see cref="Source.Features.RuntimeBootstrap.Controllers.BootstrapRunsController"/>:
/// this is a thin passthrough over two reads (the runtime row + a Take(5) over its audit
/// trail). Wrapping in commands/handlers would add four files without changing
/// behaviour. The slice stays thin and the controller talks straight to the DbContext.</para>
///
/// <para><b>Authorisation.</b> Default JWT bearer plus per-project access gating on
/// the user-facing <see cref="GetStatus"/> endpoint via
/// <see cref="OwnershipExtensions.CallerCanAccessProjectAsync"/> — SuperAdmin OR
/// project owner OR a member of the project's workspace. This is a read-only
/// observability surface consumed by the in-workspace debug panel (see
/// <c>workspace-runtime-observability</c> spec, Section E), so any workspace
/// teammate gets the same operational visibility the owner does. Both "no such
/// project" and "exists but no access" surface as <c>404</c> so cross-tenant
/// runtime existence isn't leaked. The daemon-facing <see cref="GetActiveSession"/>
/// endpoint runs under the separate <c>RuntimeToken</c> scheme and verifies the
/// token's <c>rt_runtime</c> claim matches the path id — a different auth model
/// documented inline.</para>
/// </summary>
[ApiController]
[Route("api/projects/{projectId:guid}/branches/{branchId:guid}/runtime")]
[Authorize]
[Tags("RuntimeStatus")]
public class RuntimeStatusController : ControllerBase
{
    private readonly ApplicationDbContext _db;

    public RuntimeStatusController(ApplicationDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Return the current lifecycle status of the project's most-recent (non-deleted)
    /// runtime, plus the five most recent state transitions for that runtime. 404 when
    /// no runtime exists for the project — the soft-delete query filter on
    /// <see cref="ProjectRuntime"/> means a fully torn-down runtime is invisible here,
    /// which matches the user-visible behaviour: the project shows "no runtime" and
    /// the user clicks "create".
    ///
    /// <para><b>Access gate.</b> SuperAdmin OR project owner OR workspace member
    /// of the project's owning workspace. Read-only observability — every workspace
    /// teammate sees the same state header the owner does (spec: workspace-runtime-
    /// observability, Section E). Probes for non-existent projects / projects you
    /// have no access to get <c>404</c>, never <c>403</c>, so cross-tenant
    /// project/runtime existence isn't leaked.</para>
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(RuntimeStatusResponse), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<RuntimeStatusResponse>> GetStatus(
        Guid projectId,
        Guid branchId,
        CancellationToken ct)
    {
        if (!await _db.CallerCanAccessProjectAsync(User, projectId, ct))
        {
            return NotFound();
        }

        // Most-recent (non-deleted) runtime per project+branch. A project owns one
        // ProjectRuntime per branch after CopyBranch — filtering by ProjectId alone
        // returned an arbitrary sibling branch's runtime and surfaced cross-branch
        // state in the header (e.g. "Online" for a branch whose actual runtime is
        // Failed). Soft-deleted rows are filtered out by the global query filter on
        // ProjectRuntime, so a 404 here is the right signal even after a teardown —
        // the user has nothing to look at.
        var runtime = await _db.ProjectRuntimes
            .Where(r => r.ProjectId == projectId && r.BranchId == branchId)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (runtime is null)
        {
            return NotFound();
        }

        // Five rows is the UI's "what just happened" timeline budget — bigger and the
        // header gets noisy, smaller and we miss boot-loop patterns. The compound
        // index IX_RuntimeStateEvents_RuntimeId_CreatedAt covers this exactly.
        var recentRows = await _db.RuntimeStateEvents
            .Where(e => e.RuntimeId == runtime.Id)
            .OrderByDescending(e => e.CreatedAt)
            .Take(5)
            .Select(e => new
            {
                e.FromState,
                e.ToState,
                e.Reason,
                e.TriggeredBy,
                e.Metadata,
                e.CreatedAt,
            })
            .ToListAsync(ct);

        var recent = recentRows
            .Select(e => new RuntimeTransitionDto(
                e.FromState,
                e.ToState,
                e.Reason,
                e.TriggeredBy,
                e.CreatedAt))
            .ToList();

        // When the runtime is in Failed, surface the most-recent transition's reason
        // and metadata at the top level so the UI can render an actionable error
        // banner without having to dig through RecentTransitions. Null for any other
        // state — we don't expose stale failure context once a runtime has recovered.
        string? errorReason = null;
        string? errorMessage = null;
        if (runtime.State == RuntimeState.Failed)
        {
            var failTransition = recentRows.FirstOrDefault(e => e.ToState == RuntimeState.Failed);
            if (failTransition is not null)
            {
                errorReason = failTransition.Reason;
                errorMessage = failTransition.Metadata;
            }
        }

        // Boot-issue events (self-healing-runtime-specs). When the runtime's
        // spec only partially applied (SpecHealth == Degraded) the amber banner
        // renders an expandable list of what failed. We surface the most-recent
        // boot-issue RuntimeEvents inline so the banner needs no follow-up round
        // trip. Only fetched when degraded — a healthy runtime ships an empty
        // list and skips the query entirely. The (RuntimeId, Type, Timestamp)
        // composite index covers the filtered read.
        List<RuntimeBootIssueDto> recentBootIssues = new();
        if (runtime.SpecHealth == RuntimeSpecHealth.Degraded)
        {
            var bootIssueTypes = new[]
            {
                RuntimeEventTypes.InstallFailed,
                RuntimeEventTypes.ServiceCrashed,
                RuntimeEventTypes.ServiceFailedToStart,
                RuntimeEventTypes.SpecDeltaFailed,
                RuntimeEventTypes.ServiceEnvMissing,
                RuntimeEventTypes.SpecDegraded,
            };

            var issueRows = await _db.RuntimeEvents
                .Where(e => e.RuntimeId == runtime.Id && bootIssueTypes.Contains(e.Type))
                .OrderByDescending(e => e.Timestamp)
                .Take(20)
                .Select(e => new
                {
                    e.Type,
                    e.Severity,
                    e.Timestamp,
                    e.Payload,
                })
                .ToListAsync(ct);

            recentBootIssues = issueRows
                .Select(e => new RuntimeBootIssueDto(
                    e.Type,
                    e.Severity.ToString(),
                    e.Timestamp,
                    e.Payload))
                .ToList();
        }

        // Observability snapshots — see audit item A2/A5/A11. The five "Last*"
        // columns are the heartbeat-pushed latest samples; the frontend
        // RuntimeDrawer reads them on cold open and refreshes on every poll
        // (refetchInterval: 5_000). Strings ride as-is; the daemon side owns
        // the schema and the drawer parses against the published shape.
        return Ok(new RuntimeStatusResponse(
            runtime.Id,
            runtime.State,
            runtime.StateChangedAt,
            runtime.LastHeartbeatAt,
            runtime.FlyMachineId,
            runtime.ImageDigest,
            runtime.Region,
            recent,
            errorReason,
            errorMessage,
            runtime.RespawnRetries,
            runtime.LastDiskUsedBytes,
            runtime.LastDiskTotalBytes,
            runtime.LastDiskSampledAt,
            runtime.LastSysstatsSnapshot,
            runtime.LastSupervisordSnapshot,
            runtime.SpecHealth,
            recentBootIssues));
    }

    /// <summary>
    /// Daemon-facing: a freshly respawned daemon calls this on boot to discover
    /// whether there's an in-flight <see cref="AgentSession"/> it should resume.
    /// Returns the session payload (200) when one exists, 204 when there's
    /// nothing to resume, 404 when the runtime row is gone (e.g. soft-deleted),
    /// 401 when the bearer token is missing/expired/revoked, and 403 when the
    /// caller is authenticated but the <c>rt_runtime</c> claim doesn't match the
    /// path id (a daemon may only ask about itself).
    ///
    /// <para><b>Auth.</b> Gated on <c>[Authorize(AuthenticationSchemes = "RuntimeToken")]</c>
    /// — the <see cref="RuntimeTokenAuthenticationDefaults.SchemeName"/> scheme registered
    /// in <see cref="AuthenticationExtensions.AddRuntimeTokenAuthScheme"/>. Signature, lifetime,
    /// issuer, audience, and revocation are all verified by the JWT bearer middleware before
    /// the action runs; we only need to enforce that the token's runtime id matches the path.</para>
    ///
    /// <para><b>"Active" definition.</b> The most recent <see cref="AgentSession"/>
    /// for the runtime's project where <c>Status == Running</c> and
    /// <c>CompletedAt == null</c>, ordered by <c>CreatedAt DESC</c>. We join via
    /// the <see cref="Conversation.ProjectId"/> on the session's parent
    /// conversation — there's no direct AgentSession→Runtime FK and adding one
    /// would mean a migration we don't need.</para>
    ///
    /// <para><b>Archived conversations are still considered.</b> A user might
    /// have archived a conversation while a turn is still mid-flight; the daemon
    /// must still be able to resume so we call <c>IgnoreQueryFilters()</c> on
    /// the conversation join to bypass the archived filter. Resumability is a
    /// daemon-lifecycle concern, not a UI-visibility one.</para>
    /// </summary>
    [HttpGet("/api/runtimes/{runtimeId:guid}/active-session")]
    [Authorize(AuthenticationSchemes = RuntimeTokenAuthenticationDefaults.SchemeName)]
    [ProducesResponseType(typeof(ActiveSessionResponse), 200)]
    [ProducesResponseType(204)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<ActiveSessionResponse>> GetActiveSession(
        Guid runtimeId,
        CancellationToken ct)
    {
        // The RuntimeToken JWT scheme has already validated signature + lifetime +
        // issuer/audience and consulted the revocation cache (a missing/invalid
        // token is rejected at the middleware layer with 401). We still enforce
        // that the token's runtimeId claim matches the path — a daemon may only
        // ask about itself. Mismatched claim → 403, not 401, since the caller
        // *is* authenticated, just unauthorised for this resource.
        var claimRuntimeIdRaw = User.FindFirstValue(RuntimeTokenClaimNames.RuntimeId);
        if (!Guid.TryParse(claimRuntimeIdRaw, out var claimRuntimeId) || claimRuntimeId != runtimeId)
        {
            return Forbid();
        }

        // Default query — soft-deleted runtimes are filtered out by the global
        // filter, which is exactly what we want here: a deleted runtime has no
        // resumable session.
        var runtime = await _db.ProjectRuntimes
            .FirstOrDefaultAsync(r => r.Id == runtimeId, ct);
        if (runtime is null)
        {
            return NotFound();
        }

        // IgnoreQueryFilters() on the Conversations side so an archived parent
        // conversation does not hide a still-Running session. The daemon needs
        // to resume regardless of UI visibility.
        //
        // Filter by BOTH runtime.ProjectId AND runtime.BranchId — defense in
        // depth. The RuntimeToken JWT already pinned the caller to this specific
        // runtime, so in principle a project+branch match is implied. But the
        // conversation join is the layer that decides which session is "the
        // active one for this daemon", and project alone is too loose: a project
        // with multiple branches has one ProjectRuntime per branch, each with
        // its own conversations. Joining on ProjectId only would return a
        // sibling branch's still-Running session — a silent cross-branch
        // session handoff. The BranchId filter closes that window.
        var session = await _db.AgentSessions
            .Where(s => s.Status == AgentSessionStatus.Running && s.CompletedAt == null)
            .Join(
                _db.Conversations.IgnoreQueryFilters().Where(c => c.ProjectId == runtime.ProjectId && c.BranchId == runtime.BranchId),
                s => s.ConversationId,
                c => c.Id,
                (s, c) => s)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (session is null)
        {
            return NoContent();
        }

        return Ok(new ActiveSessionResponse(
            session.Id,
            session.ConversationId,
            session.Prompt,
            session.AgentId));
    }

    /// <summary>
    /// Daemon-facing minimal liveness ping. Updates <see cref="ProjectRuntime.LastHeartbeatAt"/>
    /// and nothing else — no payload, no telemetry, no events, no SignalR fan-out.
    ///
    /// <para><b>Why a separate path from <c>RuntimeHub.Heartbeat</c>.</b> The
    /// SignalR heartbeat carries the rich payload (disk sample, sysstats,
    /// active session id, …) and is fired from the daemon's main event loop.
    /// During heavy turns the main loop can stall for tens of seconds —
    /// timers don't fire, the SignalR <c>Heartbeat</c> invoke doesn't go out,
    /// and the master's <see cref="Source.Features.RuntimeLifecycle.Jobs.HeartbeatWatcherJob"/>
    /// flags the runtime as <c>Crashed</c>. The daemon side mitigates this
    /// three ways (BoundedAsyncQueue backpressure on the emit pipeline,
    /// SelfWatchdog worker_thread that SIGKILLs on > 50 s stall, and the
    /// 60 s master threshold), but this endpoint is the fourth: a
    /// worker_thread on the daemon can fire <c>fetch()</c> at this URL
    /// independently of the main loop. As long as the worker can reach the
    /// network, the master sees liveness.</para>
    ///
    /// <para><b>Auth.</b> Same <c>RuntimeToken</c> scheme as
    /// <see cref="GetActiveSession"/>. Daemon worker presents the same token
    /// the main thread uses; the bearer-token contract is the runtime's identity,
    /// not "the SignalR connection's identity", so a second consumer over HTTP
    /// is fine. The <c>rt_runtime</c> claim must match the path id — defence
    /// in depth so a stolen token for runtime A can't ping-keep-alive runtime B.</para>
    ///
    /// <para><b>Why no payload.</b> The rich data still flows over SignalR
    /// when main is healthy. This endpoint exists only so the watchdog's
    /// liveness clock doesn't trip during a transient stall — keeping the
    /// payload empty makes the HTTP path the cheapest possible per-runtime
    /// per-tick, which matters when 100+ runtimes are pinging every 5 s.</para>
    ///
    /// <para><b>No events / no payload.</b> We deliberately do not publish
    /// domain events or service-down detection here — those flow through the
    /// rich SignalR <c>Heartbeat</c> when main is healthy. This endpoint is
    /// pure liveness: load runtime, stamp <c>LastHeartbeatAt</c>, save.
    /// <c>UpdatedAt</c> gets re-stamped by the audit interceptor — same as
    /// the SignalR path already does on every beat — so the audit semantics
    /// match exactly.</para>
    /// </summary>
    [HttpPost("/api/runtimes/{runtimeId:guid}/heartbeat-tick")]
    [Authorize(AuthenticationSchemes = RuntimeTokenAuthenticationDefaults.SchemeName)]
    [ProducesResponseType(204)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> HeartbeatTick(
        Guid runtimeId,
        CancellationToken ct)
    {
        // Defence in depth — the RuntimeToken middleware already validated
        // signature + lifetime + revocation. Make sure the bearer was issued
        // for THIS runtime (not someone else's).
        var claimRuntimeIdRaw = User.FindFirstValue(RuntimeTokenClaimNames.RuntimeId);
        if (!Guid.TryParse(claimRuntimeIdRaw, out var claimRuntimeId) || claimRuntimeId != runtimeId)
        {
            return Forbid();
        }

        // Same pattern as RuntimeHub.Heartbeat: load the runtime (the global
        // soft-delete filter hides janitor-marked rows, so a deleted runtime
        // returns null and we 404), stamp the timestamp, save. Keeping the
        // pattern identical means the heartbeat audit semantics on the row
        // are the same regardless of which transport delivered the beat.
        var runtime = await _db.ProjectRuntimes
            .FirstOrDefaultAsync(r => r.Id == runtimeId, ct);
        if (runtime is null)
        {
            return NotFound();
        }

        runtime.LastHeartbeatAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }
}

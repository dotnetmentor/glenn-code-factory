using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;
using Source.Infrastructure;
using Source.Infrastructure.AuthorizationModels;
using Source.Shared;

namespace Source.Features.RuntimeBootstrap.Controllers;

/// <summary>
/// Operator-facing entry point for kicking a stuck or partially-bootstrapped
/// runtime back through its bootstrap flow without a full Fly machine recreate.
/// Pushes <see cref="IRuntimeClient.ForceRebootstrap"/> to the
/// <c>runtime-{RuntimeId}</c> SignalR group; the daemon wipes its local
/// <c>bootstrap.json</c>, re-fetches a fresh bundle and re-runs bootstrap.
///
/// <para><b>Why no MediatR.</b> Same pragmatic reasoning as
/// <see cref="BootstrapRunsController"/>,
/// <see cref="Source.Features.Hooks.Controllers.HookConfigAdminController"/> and
/// <see cref="Source.Features.RuntimeLifecycle.Controllers.RuntimeStatusController"/>:
/// a thin existence check + SignalR push + (optional) state flip is not a
/// business feature with cross-slice events. Wrapping it in a command would
/// add four files without changing behaviour.</para>
///
/// <para><b>Authorisation.</b> Gated on <see cref="RoleConstants.SuperAdmin"/>
/// — matches the precedent in
/// <see cref="Source.Features.RuntimeLifecycle.Controllers.RuntimeProvisionController"/>:
/// admin-only entry point, paid infrastructure on the other end. The claim is
/// issued for users in the SuperAdmin role; non-admins get 403 at the
/// middleware layer before the action runs.</para>
/// </summary>
[ApiController]
[Route("api/admin/runtimes/{runtimeId:guid}/force-rebootstrap")]
[Authorize(Roles = RoleConstants.SuperAdmin)]
[Tags("RuntimeBootstrapAdmin")]
public class ForceRebootstrapAdminController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IHubContext<RuntimeHub, IRuntimeClient> _runtimeHub;
    private readonly IClock _clock;
    private readonly ILogger<ForceRebootstrapAdminController> _logger;

    public ForceRebootstrapAdminController(
        ApplicationDbContext db,
        IHubContext<RuntimeHub, IRuntimeClient> runtimeHub,
        IClock clock,
        ILogger<ForceRebootstrapAdminController> logger)
    {
        _db = db;
        _runtimeHub = runtimeHub;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Push <see cref="IRuntimeClient.ForceRebootstrap"/> to the runtime's
    /// SignalR group and (optionally) flip the runtime state back to
    /// <see cref="RuntimeState.Bootstrapping"/> when it is currently
    /// <see cref="RuntimeState.Online"/>. Returns 202 Accepted with the
    /// initiation timestamp the daemon will see in the payload.
    ///
    /// <para>Idempotent: re-calling for a runtime already mid-bootstrap simply
    /// re-pushes the command. The daemon owns idempotency on its side (replay
    /// of the same wipe-and-rerun sequence is safe).</para>
    ///
    /// <para>The state transition is best-effort: if the runtime is in any
    /// other state we leave it alone and just dispatch the command. The daemon
    /// will emit its usual lifecycle events as it walks back through bootstrap.</para>
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(ForceRebootstrapResponse), 202)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<ForceRebootstrapResponse>> Post(
        Guid runtimeId,
        [FromBody] ForceRebootstrapRequest? request,
        CancellationToken ct)
    {
        // Existence check — soft-deleted rows are filtered by the global
        // ProjectRuntime query filter, so a janitor-marked runtime falls
        // through to 404 just like a hard-missing one. Same kill-switch
        // story as RuntimeHub.OnConnectedAsync and HookConfigAdminController.
        var runtime = await _db.ProjectRuntimes
            .FirstOrDefaultAsync(r => r.Id == runtimeId, ct);
        if (runtime is null)
        {
            return NotFound();
        }

        var bootstrapStartedAt = _clock.UtcNow;
        var reason = string.IsNullOrWhiteSpace(request?.Reason)
            ? "operator_request"
            : request!.Reason!.Trim();

        // If the runtime is currently Online, walk it back to Bootstrapping so
        // the lifecycle UI reflects the in-flight rerun. Other states (Booting,
        // Bootstrapping, Crashed, Suspended, …) get the command dispatched
        // without a state change — the daemon's own lifecycle events will move
        // the row when bootstrap actually progresses.
        if (runtime.State == RuntimeState.Online)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
            var transition = runtime.TransitionTo(
                RuntimeState.Bootstrapping,
                $"force_rebootstrap:{reason}",
                $"admin:{userId}");

            if (transition.IsFailure)
            {
                // TransitionTo only fails on illegal moves, which we've narrowed
                // to "from Online" above. Log and continue — the daemon push is
                // still useful even if the audit row didn't write.
                _logger.LogWarning(
                    "ForceRebootstrap: Online -> Bootstrapping transition refused for runtime {RuntimeId}: {Error}",
                    runtimeId, transition.Error);
            }
            else
            {
                await _db.SaveChangesAsync(ct);
            }
        }

        // Push to the daemon. Best-effort: an offline daemon re-fetches the
        // (fresh) bootstrap bundle on its next reconnect anyway, so a failed
        // fan-out doesn't compromise eventual consistency. Mirrors
        // HookConfigAdminController.Update's try/catch around UpdateConfig.
        try
        {
            await _runtimeHub.Clients
                .Group($"runtime-{runtimeId}")
                .ForceRebootstrap(new ForceRebootstrapPayload(
                    Reason: reason,
                    InitiatedAt: bootstrapStartedAt));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ForceRebootstrapAdminController: ForceRebootstrap push failed for runtime {RuntimeId}; daemon will pick up a fresh bootstrap bundle on reconnect.",
                runtimeId);
        }

        _logger.LogInformation(
            "Operator dispatched ForceRebootstrap to runtime {RuntimeId} (reason={Reason}, initiatedAt={InitiatedAt:O})",
            runtimeId, reason, bootstrapStartedAt);

        return Accepted(new ForceRebootstrapResponse(runtimeId, bootstrapStartedAt));
    }
}

/// <summary>
/// Optional body for the force-rebootstrap admin endpoint. <see cref="Reason"/>
/// is recorded in the daemon's audit trail and surfaces in the runtime's
/// state-event metadata when the API also flips the state to Bootstrapping.
/// Defaults to <c>"operator_request"</c> when omitted or blank.
/// </summary>
public record ForceRebootstrapRequest(string? Reason);

/// <summary>
/// 202 response from the force-rebootstrap admin endpoint. The
/// <see cref="BootstrapStartedAt"/> instant matches the
/// <see cref="ForceRebootstrapPayload.InitiatedAt"/> the daemon will see —
/// callers can correlate operator actions with daemon-side audit rows.
/// </summary>
public record ForceRebootstrapResponse(Guid RuntimeId, DateTime BootstrapStartedAt);

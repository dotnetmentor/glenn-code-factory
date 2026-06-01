using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Source.Features.Conversations.Models;
using Source.Features.FlyManagement;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.SystemSettings.Services;
using Source.Infrastructure;
using Source.Shared;

namespace Source.Features.RuntimeLifecycle.Jobs;

/// <summary>
/// Recurring Hangfire job that detects <see cref="RuntimeState.Online"/> runtimes
/// nobody has interacted with for a while and transitions them to
/// <see cref="RuntimeState.Suspending"/>, then asks Fly to stop the underlying
/// machine. The user-driven inverse — a fresh tab waking the machine back up —
/// lives in <see cref="SignalR.Hubs.AgentHub.OnConnectedAsync"/> via
/// <see cref="Commands.WakeRuntimeOnConnectCommand"/>. Together they form the
/// idle/wake half of the runtime-lifecycle spec.
///
/// <para><b>Cadence.</b> Mirrors <see cref="HeartbeatWatcherJob"/>: registered
/// minutely with Hangfire (smallest built-in cadence), the body fans out as
/// 12 x 5-second iterations so the effective scan cadence is 5 s without
/// paying the per-second registration tax. Hangfire backpressure plus
/// <see cref="DisableConcurrentExecutionAttribute"/> guards against two workers
/// overlapping on the same minute.</para>
///
/// <para><b>What "idle" means.</b> A runtime is considered idle when its most
/// recent <see cref="Conversation.LastActivityAt"/> on the project is older
/// than the threshold (and there is no active in-flight
/// <see cref="AgentSession"/>). <see cref="ProjectRuntime.LastHeartbeatAt"/> is
/// not a useful idleness signal on its own — daemons heartbeat continuously,
/// even when the user isn't there. A fresh runtime with no conversations yet
/// uses <see cref="ProjectRuntime.StateChangedAt"/> as the floor so we don't
/// suspend it immediately on a slow first-prompt window.</para>
///
/// <para><b>Threshold.</b> Read from
/// <c>RuntimeLifecycle:IdleThresholdMinutes</c> on every iteration so an
/// operator change on the System Settings page takes effect within five
/// seconds. Default 30 minutes when unset / unparseable. A per-runtime
/// override on <see cref="ProjectRuntime.IdleThresholdMinutes"/> shadows the
/// global value.</para>
///
/// <para><b>Failure isolation.</b> Per-iteration <c>try/catch</c> ensures one
/// bad scan iteration doesn't kill the remaining 11; per-row
/// transition / Fly call failures are logged at warning level and skipped so
/// the rest of the batch still progresses.</para>
/// </summary>
public class IdlerJob
{
    /// <summary>How many times the inner loop fires per Hangfire invocation.</summary>
    private const int LoopIterations = 12;

    /// <summary>Sleep between inner-loop iterations.</summary>
    private const int LoopIntervalSeconds = 5;

    /// <summary>Default threshold when SystemSettings doesn't supply one.</summary>
    private const int DefaultIdleThresholdMinutes = 30;

    /// <summary>SystemSettings key for the global idle threshold.</summary>
    public const string IdleThresholdSettingKey = "RuntimeLifecycle:IdleThresholdMinutes";

    private readonly ApplicationDbContext _db;
    private readonly FlyClient _fly;
    private readonly ISystemSettingsService _settings;
    private readonly IClock _clock;
    private readonly ILogger<IdlerJob> _logger;

    public IdlerJob(
        ApplicationDbContext db,
        FlyClient fly,
        ISystemSettingsService settings,
        IClock clock,
        ILogger<IdlerJob> logger)
    {
        _db = db;
        _fly = fly;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Hangfire entry point. Wraps the inner <see cref="Run(CancellationToken)"/>
    /// in a linked <see cref="CancellationTokenSource"/> with a hard 50-second
    /// budget so the job can never hold the
    /// <see cref="DisableConcurrentExecutionAttribute"/> lock past the 60-second
    /// TTL — even if every external call hangs forever. The runtime budget is
    /// shorter than the lock TTL on purpose: when the CTS trips, control
    /// returns, Hangfire releases the lock cleanly, and the next tick acquires
    /// on schedule.
    ///
    /// <para><see cref="AutomaticRetry"/> with <c>Attempts = 0</c> stops Hangfire
    /// from auto-requeuing a partially-cancelled run on top of the next scheduled
    /// tick.</para>
    ///
    /// <para><b>OperationCanceledException at the budget boundary is swallowed;
    /// real shutdown still bubbles.</b> The 12 x 5s loop intentionally outruns
    /// the 50s budget on the final <see cref="Task.Delay(TimeSpan, CancellationToken)"/>
    /// so cancellation fires while the worker is asleep — that's the planned
    /// mechanism for ending the loop. We catch only the cancellation that came
    /// from our budget CTS (i.e. not from the Hangfire shutdown token) so a real
    /// host shutdown still bubbles to Hangfire and the worker releases its lock.</para>
    /// </summary>
    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    [AutomaticRetry(Attempts = 0)]
    public async Task Run(IJobCancellationToken hangfireCt)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(hangfireCt.ShutdownToken);
        cts.CancelAfter(TimeSpan.FromSeconds(50));
        try
        {
            await Run(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !hangfireCt.ShutdownToken.IsCancellationRequested)
        {
            // Expected end-of-budget cancellation. The 12 x 5s loop intentionally
            // outruns the 50s budget on the final delay so cancellation can fire
            // while the worker is asleep. Returning cleanly lets Hangfire release
            // the DisableConcurrentExecution lock on time.
            _logger.LogDebug("IdlerJob hit 50s budget — returning cleanly so the lock releases on time");
        }
    }

    public async Task Run(CancellationToken ct = default)
    {
        for (int i = 0; i < LoopIterations && !ct.IsCancellationRequested; i++)
        {
            try
            {
                await ScanOnce(ct);
            }
            // Filter out OCE-from-our-budget so we don't log it at Error level on
            // the final iteration when the 50s budget CTS fires mid-scan. Real
            // host shutdown still bubbles to the outer try/catch as a clean
            // OperationCanceledException (handled there with LogDebug).
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "IdlerJob scan iteration {Iteration} failed", i);
            }

            if (i < LoopIterations - 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(LoopIntervalSeconds), ct);
            }
        }
    }

    /// <summary>
    /// Single scan pass. Public so tests can target it directly without the
    /// 55-second sleep budget the loop carries.
    /// </summary>
    public async Task ScanOnce(CancellationToken ct = default)
    {
        var globalThreshold = ReadThresholdFromSettings();
        var nowUtc = _clock.UtcNow;

        // Online runtimes only — Suspended / Suspending / Booting / Crashed all
        // have their own owners (idler, reconciler, watcher). The idler's job is
        // exactly the steady-state Online → Suspending edge.
        var onlineRuntimes = await _db.ProjectRuntimes
            .Where(r => r.State == RuntimeState.Online)
            .ToListAsync(ct);

        if (onlineRuntimes.Count == 0)
        {
            return;
        }

        // Fetch the latest activity timestamp per project in one batch — we
        // join against a small set of conversations so the per-runtime loop
        // doesn't issue N queries.
        var projectIds = onlineRuntimes.Select(r => r.ProjectId).Distinct().ToList();
        var lastActivityByProject = await _db.Conversations
            .Where(c => projectIds.Contains(c.ProjectId))
            .GroupBy(c => c.ProjectId)
            .Select(g => new { ProjectId = g.Key, LastActivityAt = g.Max(c => c.LastActivityAt) })
            .ToDictionaryAsync(x => x.ProjectId, x => x.LastActivityAt, ct);

        // In-flight sessions hold a runtime open even past the threshold —
        // suspend mid-turn would lose work. We treat any non-terminal session
        // for a runtime's project as "the user is here, just slow."
        var activeSessionProjectIds = await _db.AgentSessions
            .Where(s => s.Status == AgentSessionStatus.Pending
                     || s.Status == AgentSessionStatus.Running)
            .Join(_db.Conversations,
                  s => s.ConversationId,
                  c => c.Id,
                  (s, c) => c.ProjectId)
            .Where(pid => projectIds.Contains(pid))
            .Distinct()
            .ToListAsync(ct);
        var activeSessionLookup = new HashSet<Guid>(activeSessionProjectIds);

        var suspended = 0;
        foreach (var runtime in onlineRuntimes)
        {
            // Per-runtime override shadows the global threshold. Negative /
            // zero values are treated as "use default" — operator UX guard.
            var thresholdMinutes = (runtime.IdleThresholdMinutes is { } overrideMin && overrideMin > 0)
                ? overrideMin
                : globalThreshold;

            if (activeSessionLookup.Contains(runtime.ProjectId))
            {
                continue;
            }

            // Idleness floor: most recent conversation activity on the project,
            // or the moment the runtime entered Online if no conversation has
            // ever happened. Without the floor, a brand-new runtime with no
            // conversations would look infinitely idle.
            var idleSince = lastActivityByProject.TryGetValue(runtime.ProjectId, out var lastActivity)
                ? (lastActivity > runtime.StateChangedAt ? lastActivity : runtime.StateChangedAt)
                : runtime.StateChangedAt;

            var idleSeconds = (int)(nowUtc - idleSince).TotalSeconds;
            if (idleSeconds < thresholdMinutes * 60)
            {
                continue;
            }

            await SuspendOne(runtime, idleSince, thresholdMinutes, ct);
            suspended++;
        }

        if (suspended > 0)
        {
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation(
                "IdlerJob suspended {Count} runtimes (global threshold {Minutes} min).",
                suspended, globalThreshold);
        }
    }

    private async Task SuspendOne(
        ProjectRuntime runtime,
        DateTime idleSince,
        int thresholdMinutes,
        CancellationToken ct)
    {
        var metadata = JsonSerializer.Serialize(new
        {
            idleSince,
            thresholdMinutes,
            // Snapshot the policy that fired this transition for the audit log;
            // useful when debugging "why did this runtime nap?" weeks later.
            triggeredByJob = "idler",
        });

        var transition = runtime.TransitionTo(
            RuntimeState.Suspending,
            "idle:threshold_exceeded",
            "watcher:idler",
            metadata);

        if (!transition.IsSuccess)
        {
            _logger.LogWarning(
                "IdlerJob could not transition runtime {RuntimeId} from {State} to Suspending: {Error}",
                runtime.Id, runtime.State, transition.Error);
            return;
        }

        // Best-effort Fly call. FlyClient writes its own FlyOperation audit row
        // and surfaces transport errors as exceptions; we swallow + log so a
        // single Fly blip doesn't abort the whole iteration.
        if (!string.IsNullOrEmpty(runtime.FlyMachineId))
        {
            try
            {
                await _fly.StopMachineAsync(
                    machineId: runtime.FlyMachineId,
                    options: null,
                    runtimeId: runtime.Id,
                    ct: ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "IdlerJob Fly StopMachine call failed for machine {MachineId} (runtime {RuntimeId}); reconciler will retry.",
                    runtime.FlyMachineId, runtime.Id);
            }
        }
        else
        {
            _logger.LogWarning(
                "IdlerJob: runtime {RuntimeId} has no FlyMachineId; transitioned to Suspending but no Fly call issued.",
                runtime.Id);
        }
    }

    private int ReadThresholdFromSettings()
    {
        var raw = _settings.Get(IdleThresholdSettingKey);
        if (!string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw, out var parsed)
            && parsed > 0)
        {
            return parsed;
        }
        return DefaultIdleThresholdMinutes;
    }
}

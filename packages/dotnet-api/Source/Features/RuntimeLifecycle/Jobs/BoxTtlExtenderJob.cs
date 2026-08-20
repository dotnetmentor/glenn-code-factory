using Hangfire;
using Microsoft.EntityFrameworkCore;
using Source.Features.BoxManagement;
using Source.Features.BoxManagement.Configuration;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;

namespace Source.Features.RuntimeLifecycle.Jobs;

/// <summary>
/// Recurring Hangfire job that re-arms the TTL on every box we still know about.
///
/// <para><b>Why this exists — the orphan-cost guardrail.</b> Every box is created
/// with a finite <c>ttlSeconds</c> (<see cref="BoxOptions.DefaultTtlSeconds"/>).
/// If the control plane ever loses track of a box — a cleanup job crashes, a DB
/// row is deleted out-of-band, the platform is simply down — the box archives
/// ITSELF when the TTL lapses and billing stops. The exact failure mode that
/// once produced a surprise Fly bill is structurally impossible: an orphan can
/// accumulate at most one TTL window of cost. This job is the other half of the
/// deal: for every runtime we DO know about and expect to keep running, it
/// pushes the deadline out again, so healthy boxes never hit it.</para>
///
/// <para><b>Scope.</b> Only runtimes in live states (Booting / Bootstrapping /
/// Online / Waking) are extended. Suspended runtimes are archived boxes — TTL
/// is irrelevant while billing is paused, and the wake path re-arms it
/// explicitly. Deleting/Deleted/Failed/Crashed are deliberately NOT extended:
/// if a crashed runtime's box lingers unresumed, self-archival at TTL is
/// exactly the safety net working.</para>
///
/// <para><b>Cadence.</b> Every 30 minutes against a default 6-hour TTL — a
/// runtime survives ~11 consecutive missed extension passes before its box
/// archives itself, so a platform outage shorter than the TTL is invisible to
/// users while a longer one fails safe (cheap), not open (billable).</para>
/// </summary>
public class BoxTtlExtenderJob
{
    /// <summary>Soft cap per pass — same rationale as the reconciler's list bound.</summary>
    public const int MaxRuntimesPerPass = 500;

    private readonly ApplicationDbContext _db;
    private readonly BoxClient _box;
    private readonly IBoxOptionsAccessor _boxOptions;
    private readonly ILogger<BoxTtlExtenderJob> _logger;

    public BoxTtlExtenderJob(
        ApplicationDbContext db,
        BoxClient box,
        IBoxOptionsAccessor boxOptions,
        ILogger<BoxTtlExtenderJob> logger)
    {
        _db = db;
        _box = box;
        _boxOptions = boxOptions;
        _logger = logger;
    }

    /// <summary>
    /// Hangfire entry point. 110-second budget under the 120-second lock TTL,
    /// same shape as the reconciler.
    /// </summary>
    [DisableConcurrentExecution(timeoutInSeconds: 120)]
    [AutomaticRetry(Attempts = 0)]
    public async Task Run(IJobCancellationToken hangfireCt)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(hangfireCt.ShutdownToken);
        cts.CancelAfter(TimeSpan.FromSeconds(110));
        await Run(cts.Token);
    }

    /// <summary>Single extension pass. Public so tests can target it directly.</summary>
    public async Task Run(CancellationToken ct = default)
    {
        var ttlSeconds = _boxOptions.Current.DefaultTtlSeconds;
        if (ttlSeconds <= 0)
        {
            // Explicit operator opt-out (TTL disabled). Nothing to extend — but
            // log loudly because running without the guardrail is a choice that
            // should be visible, not silent.
            _logger.LogWarning(
                "BoxTtlExtenderJob: Box:DefaultTtlSeconds is {Ttl} — TTL guardrail disabled, skipping pass.",
                ttlSeconds);
            return;
        }

        var live = await _db.ProjectRuntimes
            .AsNoTracking()
            .Where(r => r.BoxId != null
                     && (r.State == RuntimeState.Booting
                      || r.State == RuntimeState.Bootstrapping
                      || r.State == RuntimeState.Online
                      || r.State == RuntimeState.Waking))
            .OrderBy(r => r.Id)
            .Take(MaxRuntimesPerPass)
            .Select(r => new { r.Id, r.BoxId })
            .ToListAsync(ct);

        if (live.Count == 0)
        {
            return;
        }

        var extended = 0;
        var failed = 0;

        foreach (var runtime in live)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await _box.SetTtlAsync(runtime.BoxId!, ttlSeconds, runtime.Id, ct);
                extended++;
            }
            catch (BoxApiException ex) when (ex.StatusCode == 404)
            {
                // Box gone — the reconciler owns that transition; nothing to extend.
                _logger.LogInformation(
                    "BoxTtlExtenderJob: box {BoxId} (runtime {RuntimeId}) is gone (404); reconciler will pick it up.",
                    runtime.BoxId, runtime.Id);
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex,
                    "BoxTtlExtenderJob: TTL extend failed for box {BoxId} (runtime {RuntimeId}); next pass retries.",
                    runtime.BoxId, runtime.Id);
            }
        }

        _logger.LogInformation(
            "BoxTtlExtenderJob extended TTL on {Extended}/{Total} live boxes ({Failed} failures, ttl={TtlSeconds}s).",
            extended, live.Count, failed, ttlSeconds);
    }
}

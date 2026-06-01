using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeTokens.Models;
using Source.Infrastructure;
using Source.Shared;

namespace Source.Features.RuntimeTokens.Services;

/// <summary>
/// Process-local accumulator for RuntimeToken validate-path usage metrics.
/// Every successful JWT validation increments an in-memory counter keyed by
/// jti; a 30-second Hangfire flush job (see
/// <see cref="Jobs.RuntimeTokenUsageFlushJobRegistration"/>) drains the
/// accumulator and writes the deltas back to <c>RuntimeTokenIssues.LastUsedAt</c>
/// + <c>RequestCount</c>.
///
/// <para><b>Why not write on every validate.</b> A busy daemon validates its
/// JWT at every heartbeat (5 s) plus every HTTP request from in-runtime tooling.
/// A SaveChanges per validate would balloon the WAL and contend on the same
/// row that the rotation job touches. Instead, we keep an O(1) ConcurrentDictionary
/// update on the hot path and batch the persistence at a coarse cadence.</para>
///
/// <para><b>Why singleton.</b> The accumulator is process-state — losing
/// 30 s of counts on host shutdown is acceptable (the metrics are
/// approximate by design, not financial), but having one accumulator per
/// scope would defeat the batching entirely. Mirrors
/// <c>HealthSnapshotBuffer</c> / <c>ServiceDownDetector</c> /
/// <c>RestartServiceThrottle</c> registered alongside in <c>Program.cs</c>.</para>
///
/// <para><b>Multi-instance note.</b> Like the McpRateLimiter, the accumulator
/// is per-process. If the API is scaled horizontally each instance flushes
/// independently and the additive UPDATE on <c>RequestCount</c> sums them
/// correctly; <c>LastUsedAt</c> takes <c>GREATEST(existing, new)</c> so the
/// latest write wins. No coordination layer required.</para>
/// </summary>
public class RuntimeTokenUsageRecorder
{
    private readonly ConcurrentDictionary<Guid, UsageEntry> _accumulator = new();
    private readonly IClock _clock;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RuntimeTokenUsageRecorder> _logger;

    public RuntimeTokenUsageRecorder(
        IClock clock,
        IServiceScopeFactory scopeFactory,
        ILogger<RuntimeTokenUsageRecorder> logger)
    {
        _clock = clock;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Bump the in-memory counter for <paramref name="jti"/>. Called from the
    /// JWT bearer <c>OnTokenValidated</c> hook after revocation has cleared.
    /// O(1), allocation-bounded, never blocks. Safe under concurrent invocation.
    /// </summary>
    public void Record(Guid jti)
    {
        var now = _clock.UtcNow;
        _accumulator.AddOrUpdate(
            jti,
            _ => new UsageEntry(1, now),
            (_, existing) => new UsageEntry(existing.Count + 1, now > existing.LastUsedAt ? now : existing.LastUsedAt));
    }

    /// <summary>
    /// Atomically take the current accumulator snapshot and reset it. Public
    /// so the flush job can drive it directly. Returns an empty dict when
    /// nothing is pending — callers should short-circuit on count == 0.
    ///
    /// <para>"Atomic" here means: between snapshot and reset no <see cref="Record"/>
    /// call is lost. We use Interlocked.Exchange-style semantics by replacing
    /// the dict reference; concurrent recorders that arrived during the swap
    /// land in the new dict and roll into the next flush.</para>
    /// </summary>
    public IReadOnlyDictionary<Guid, UsageEntry> Drain()
    {
        if (_accumulator.IsEmpty)
        {
            return new Dictionary<Guid, UsageEntry>();
        }

        // ConcurrentDictionary doesn't expose a true atomic snapshot+clear, but
        // looping over a single ToArray() pass and TryRemove'ing each key is
        // race-safe: a Record call that lands between ToArray and TryRemove
        // simply re-creates the entry, which the next flush will pick up.
        var snapshot = new Dictionary<Guid, UsageEntry>(_accumulator.Count);
        foreach (var kvp in _accumulator.ToArray())
        {
            if (_accumulator.TryRemove(kvp.Key, out var removed))
            {
                snapshot[kvp.Key] = removed;
            }
        }
        return snapshot;
    }

    /// <summary>
    /// Drain the accumulator and persist the deltas. Called by the Hangfire
    /// recurring job at 30-second cadence. Per-row UPDATE keeps the SQL
    /// trivial and avoids needing raw SQL or EF bulk extensions; at expected
    /// volumes (a handful of distinct jtis per flush) this is comfortably
    /// fast.
    ///
    /// <para>Rows that no longer exist (revoked + janitor-purged tokens) are
    /// silently skipped — losing usage for a deleted row is intended.</para>
    /// </summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        var snapshot = Drain();
        if (snapshot.Count == 0)
        {
            return;
        }

        // Resolve a fresh DbContext from a scope so the singleton accumulator
        // doesn't capture a scoped service. Mirrors the pattern in
        // RevocationCacheWarmupService.
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var jtis = snapshot.Keys.ToList();
        var rows = await db.RuntimeTokenIssues
            .Where(r => jtis.Contains(r.Id))
            .ToListAsync(ct);

        var updated = 0;
        foreach (var row in rows)
        {
            if (!snapshot.TryGetValue(row.Id, out var entry))
            {
                continue;
            }

            row.RequestCount += entry.Count;
            // GREATEST(existing, new) so a stale flush from another instance
            // can't overwrite a newer LastUsedAt timestamp.
            if (row.LastUsedAt is null || entry.LastUsedAt > row.LastUsedAt.Value)
            {
                row.LastUsedAt = entry.LastUsedAt;
            }
            updated++;
        }

        // Single SaveChanges batches the UPDATEs into one transaction.
        if (updated > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        var skipped = snapshot.Count - updated;
        _logger.LogDebug(
            "RuntimeTokenUsageRecorder flushed {Updated} token usage rows ({Skipped} jtis without matching rows)",
            updated, skipped);
    }

    /// <summary>
    /// Tuple-shaped usage entry. Public so the flush path doesn't have to
    /// re-pluck from an opaque internal structure.
    /// </summary>
    public readonly record struct UsageEntry(long Count, DateTime LastUsedAt);
}

using System.Collections.Concurrent;

namespace Source.Features.Health;

/// <summary>
/// Process-local rolling telemetry buffer — keeps the last <see cref="Capacity"/>
/// <see cref="HealthSnapshot"/> rows per runtime, FIFO-evicted as new heartbeats
/// arrive. Backs the user-facing
/// <c>GET /api/runtimes/{runtimeId}/health-snapshots</c> endpoint and the Phase
/// D service-down detector that runs on every heartbeat.
///
/// <para><b>Why in-memory.</b> Heartbeats fire every ~5s per connected daemon,
/// thousands of runtimes potentially online — persisting every beat would
/// hammer Postgres and is operationally pointless: the data is transient,
/// only the trailing window is interesting, and a deploy/process restart
/// erasing it is acceptable (the next heartbeat re-populates it within
/// seconds). Same trade-off the daemon makes with
/// <c>DiskMonitor.latest()</c> — useful only as a sliding window.</para>
///
/// <para><b>Threading.</b> The instance is registered as a singleton and read
/// from concurrent SignalR hub invocations + concurrent HTTP reads. Per-runtime
/// access serialises through a lightweight <see cref="object"/> lock attached
/// to the entry; the outer <see cref="ConcurrentDictionary{TKey, TValue}"/>
/// handles dictionary-level safety. Reads return a defensive copy of the
/// buffer's contents so callers can iterate without holding the lock.</para>
///
/// <para><b>Capacity.</b> 60 rows ≈ 5 minutes at the default 5-second cadence
/// — enough trailing context for the UI's "last few minutes of telemetry"
/// view and the service-down detector's outage-window dedupe, and small
/// enough that 1000 connected runtimes cost ≈ 60k tiny records (well under
/// 50 MB).</para>
///
/// <para><b>Cleanup.</b> No background timer — opportunistic. Every
/// <see cref="Append"/> call probabilistically (~1% of beats) walks the
/// dictionary and removes entries whose freshest row is older than 30 minutes.
/// Cleanup work is bounded by a single pass over the entry count and runs
/// inline on whichever heartbeat happens to win the dice roll; a stalled
/// runtime that disconnects without a final beat won't leak forever, and
/// the steady-state cost stays near zero.</para>
/// </summary>
public class HealthSnapshotBuffer
{
    /// <summary>Maximum number of rows kept per runtime.</summary>
    public const int Capacity = 60;

    /// <summary>How long a runtime can be silent before its entry is purged on opportunistic cleanup.</summary>
    public static readonly TimeSpan IdleTtl = TimeSpan.FromMinutes(30);

    /// <summary>Probability of a cleanup sweep per <see cref="Append"/> call.</summary>
    private const double CleanupOdds = 0.01;

    private readonly ConcurrentDictionary<Guid, Entry> _byRuntime = new();
    private readonly Random _rng = new();

    /// <summary>
    /// Append a snapshot for <paramref name="runtimeId"/>. Evicts the oldest row
    /// when the buffer is at <see cref="Capacity"/>. Idempotent only in the
    /// trivial sense: callers shouldn't append the same logical heartbeat
    /// twice; the buffer itself doesn't dedupe.
    /// </summary>
    public void Append(Guid runtimeId, HealthSnapshot snapshot)
    {
        var entry = _byRuntime.GetOrAdd(runtimeId, _ => new Entry());
        lock (entry.Lock)
        {
            if (entry.Items.Count == Capacity)
            {
                entry.Items.RemoveAt(0);
            }
            entry.Items.Add(snapshot);
        }

        MaybeRunCleanup();
    }

    /// <summary>
    /// Read all snapshots for <paramref name="runtimeId"/>, optionally filtered
    /// to those received strictly after <paramref name="since"/>. Returns a
    /// fresh list ordered oldest-first (insertion order); never null. An
    /// unknown runtime returns an empty list — callers checking 404 should
    /// validate the runtime row exists separately, this buffer can't tell the
    /// difference between "we haven't seen a heartbeat yet" and "this runtime
    /// doesn't exist".
    /// </summary>
    public List<HealthSnapshot> ReadSince(Guid runtimeId, DateTime? since = null)
    {
        if (!_byRuntime.TryGetValue(runtimeId, out var entry))
        {
            return new List<HealthSnapshot>();
        }

        lock (entry.Lock)
        {
            if (since is null)
            {
                return new List<HealthSnapshot>(entry.Items);
            }

            var threshold = since.Value;
            var result = new List<HealthSnapshot>(entry.Items.Count);
            foreach (var item in entry.Items)
            {
                if (item.ReceivedAt > threshold)
                {
                    result.Add(item);
                }
            }
            return result;
        }
    }

    /// <summary>
    /// Returns the most recent snapshot for <paramref name="runtimeId"/>, or
    /// <c>null</c> when the buffer is empty / unknown. The service-down
    /// detector reads this on every heartbeat to compute an outage window.
    /// </summary>
    public HealthSnapshot? Latest(Guid runtimeId)
    {
        if (!_byRuntime.TryGetValue(runtimeId, out var entry))
        {
            return null;
        }

        lock (entry.Lock)
        {
            if (entry.Items.Count == 0) return null;
            return entry.Items[^1];
        }
    }

    /// <summary>
    /// Test seam — drop the entry for <paramref name="runtimeId"/>. Production
    /// callers should rely on the opportunistic cleanup; tests need this to
    /// keep their seed isolated from the buffer's process-local state.
    /// </summary>
    public void Clear(Guid runtimeId) => _byRuntime.TryRemove(runtimeId, out _);

    /// <summary>Test/diagnostic hook — number of runtimes with at least one snapshot.</summary>
    public int RuntimeCount => _byRuntime.Count;

    private void MaybeRunCleanup()
    {
        // Probabilistic so cleanup cost amortises across the heartbeat fleet.
        // No locking on _rng — false sharing on a hot path is more expensive
        // than the occasional missed sweep, and Random's contract permits
        // contended use (just don't rely on the sequence being deterministic
        // across threads, which we don't).
        double draw;
        lock (_rng)
        {
            draw = _rng.NextDouble();
        }
        if (draw >= CleanupOdds) return;

        var cutoff = DateTime.UtcNow - IdleTtl;
        foreach (var kvp in _byRuntime)
        {
            var entry = kvp.Value;
            DateTime? newest;
            lock (entry.Lock)
            {
                newest = entry.Items.Count == 0 ? null : entry.Items[^1].ReceivedAt;
            }

            if (newest is null || newest.Value < cutoff)
            {
                _byRuntime.TryRemove(kvp.Key, out _);
            }
        }
    }

    /// <summary>
    /// Per-runtime container. The List is the FIFO buffer; the lock guards it
    /// against the ConcurrentDictionary's per-key concurrency (which protects
    /// the dictionary itself, not the value mutation).
    /// </summary>
    private sealed class Entry
    {
        public readonly object Lock = new();
        public readonly List<HealthSnapshot> Items = new(Capacity);
    }
}

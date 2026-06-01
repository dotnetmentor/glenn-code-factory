using System.Collections.Concurrent;

namespace Source.Features.Health.Services;

/// <summary>
/// Token-bucket-ish throttle for RestartService dispatches: at most
/// <see cref="MaxDispatches"/> dispatches per <see cref="Window"/> per
/// <c>(runtimeId, serviceName)</c>. Used by
/// <see cref="EventHandlers.DispatchRestartServiceHandler"/> to keep a
/// flapping service from machine-gunning the daemon with restart commands.
///
/// <para><b>Why this shape, not full token bucket.</b> A real bucket
/// over-engineers the use case — services either come back after one restart
/// or they don't. A simple "did we already burn 3 attempts in the last 5
/// minutes" check is enough to avoid the pathological case (daemon emits
/// "service X down" every heartbeat, hub emits "restart X" every heartbeat,
/// the restart never works because the service config is wrong, runaway).</para>
///
/// <para><b>Threading.</b> The outer <see cref="ConcurrentDictionary{TKey, TValue}"/>
/// handles per-key concurrency at the dictionary level; the per-key window
/// state is a list guarded by a <see cref="object"/> lock. Dispatch hot-path
/// is bounded by a single trim + count + add over a list of at most
/// <see cref="MaxDispatches"/> entries — O(1).</para>
/// </summary>
public class RestartServiceThrottle
{
    /// <summary>Max dispatches allowed in a sliding window.</summary>
    public const int MaxDispatches = 3;

    /// <summary>Sliding window length.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<(Guid RuntimeId, string ServiceName), Entry> _byKey = new();

    /// <summary>
    /// Try to claim a dispatch slot for this <paramref name="runtimeId"/> +
    /// <paramref name="serviceName"/>. Returns <c>true</c> when the dispatch
    /// is allowed; <c>false</c> when the cap has been hit and the caller
    /// should drop / log instead. The successful claim records
    /// <paramref name="now"/> in the window; it's the caller's job to
    /// actually fire the SignalR push.
    /// </summary>
    public bool TryClaim(Guid runtimeId, string serviceName, DateTime now)
    {
        var entry = _byKey.GetOrAdd((runtimeId, serviceName), _ => new Entry());
        lock (entry.Lock)
        {
            // Trim entries older than the window so the count below is accurate.
            var cutoff = now - Window;
            entry.Timestamps.RemoveAll(t => t < cutoff);

            if (entry.Timestamps.Count >= MaxDispatches)
            {
                return false;
            }

            entry.Timestamps.Add(now);
            return true;
        }
    }

    /// <summary>Test seam — drop all throttle state for one runtime.</summary>
    public void Clear(Guid runtimeId)
    {
        foreach (var key in _byKey.Keys)
        {
            if (key.RuntimeId == runtimeId)
            {
                _byKey.TryRemove(key, out _);
            }
        }
    }

    private sealed class Entry
    {
        public readonly object Lock = new();
        public readonly List<DateTime> Timestamps = new(MaxDispatches + 1);
    }
}

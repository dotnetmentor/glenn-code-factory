using System.Collections.Concurrent;
using Source.Features.Health.Events;
using Source.Features.RuntimeCuration;

namespace Source.Features.Health.Services;

/// <summary>
/// Compares a runtime's declared spec against the live "services up" list
/// reported on every heartbeat. Emits a <see cref="RuntimeServiceDown"/> event
/// the first time a required service is observed missing, and suppresses
/// further events until the daemon reports the service back up — so a
/// long-lived outage produces one event, not one per heartbeat.
///
/// <para><b>State.</b> A <see cref="ConcurrentDictionary{TKey, TValue}"/>
/// keyed by <c>(runtimeId, serviceName)</c> tracks the start of each
/// outage window. When a service that was down is observed up again, its
/// entry is removed — the next "down" sighting fires a fresh event.
/// Process-local state, same as <see cref="HealthSnapshotBuffer"/>: a
/// process restart "forgets" outage windows, but the next heartbeat with
/// the service still down re-fires the event, which is the right behaviour
/// (an operator looking after a fresh deploy wants to know the service is
/// still flapping).</para>
///
/// <para><b>Spec parsing.</b> Delegates to <see cref="SpecDelta.ParseOrEmpty"/>
/// — the same tolerant V2 reader curation handlers use, so the contract is
/// consistent: an empty / malformed / null spec yields an empty services
/// list and the detector silently emits nothing.</para>
///
/// <para><b>Why a separate type.</b> The hub already does too much; the
/// detector's outage-tracking state must outlive a single hub instance
/// (singleton lifetime), and isolating it makes unit-testing the
/// transitions trivial without spinning up a hub.</para>
/// </summary>
public class ServiceDownDetector
{
    private readonly ConcurrentDictionary<(Guid RuntimeId, string ServiceName), DateTime> _outageStart = new();

    /// <summary>
    /// Compute the set of fresh <see cref="RuntimeServiceDown"/> events to
    /// raise for this heartbeat. Returns an empty list if everything required
    /// is up, or if all currently-down services were already in an outage
    /// window from a previous beat.
    ///
    /// <para><paramref name="specJson"/> is the runtime's persisted spec
    /// (typically <c>ProjectRuntime.Spec</c>). Null / empty / malformed
    /// → no required services → returns empty list.</para>
    ///
    /// <para><paramref name="reportedUp"/> is the daemon's
    /// <c>SupervisedServicesUp</c> field verbatim. Compared
    /// case-insensitively because supervisord sometimes reports the
    /// program name in mixed case while the spec uses lower-case
    /// canonical names.</para>
    /// </summary>
    public IReadOnlyList<RuntimeServiceDown> Detect(
        Guid runtimeId,
        string? specJson,
        IReadOnlyList<string> reportedUp,
        DateTime now)
    {
        var spec = SpecDelta.ParseOrEmpty(specJson);
        var services = spec.Services;
        if (services is null || services.Count == 0)
        {
            return Array.Empty<RuntimeServiceDown>();
        }

        var upSet = new HashSet<string>(reportedUp, StringComparer.OrdinalIgnoreCase);
        var fresh = new List<RuntimeServiceDown>();

        foreach (var svc in services)
        {
            var service = svc.Name;
            var key = (RuntimeId: runtimeId, ServiceName: service);
            if (upSet.Contains(service))
            {
                // Service is back / never went down — clear any tracked outage.
                _outageStart.TryRemove(key, out _);
                continue;
            }

            // Service is required but absent from the up-list. Only emit a fresh
            // event if we don't already know about an outage in progress —
            // TryAdd returns false on a key that's already present.
            if (_outageStart.TryAdd(key, now))
            {
                fresh.Add(new RuntimeServiceDown(runtimeId, service, now));
            }
        }

        return fresh;
    }

    /// <summary>
    /// Test seam — drop all outage state for <paramref name="runtimeId"/>.
    /// Production code never calls this; the dictionary self-prunes via
    /// <see cref="Detect"/> when services come back up.
    /// </summary>
    public void Clear(Guid runtimeId)
    {
        foreach (var key in _outageStart.Keys)
        {
            if (key.RuntimeId == runtimeId)
            {
                _outageStart.TryRemove(key, out _);
            }
        }
    }
}

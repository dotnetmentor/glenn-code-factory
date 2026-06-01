using System.Security.Cryptography;
using System.Text;
using Source.Features.RuntimeBootstrap.Contracts;
using Tapper;

namespace Source.Features.RuntimeCuration;

/// <summary>
/// Pure helpers for computing a structural diff between two
/// <see cref="RuntimeSpecV2"/> documents. The result drives the
/// daemon-bound <c>ApplyRuntimeSpecDelta</c> push: what changed since the
/// runtime's last-known spec, so the daemon can react minimally without
/// re-running every install/setup step.
///
/// <para><b>Shape.</b> See <see cref="RuntimeSpecDeltaV2"/>: per-service
/// add/change/remove buckets plus a hash-based change flag for the
/// top-level <c>install</c> and <c>setup</c> strings. <see cref="RuntimeSpecDeltaV2.HasChanges"/>
/// is the cheap "anything to push?" check.</para>
///
/// <para><b>Service matching.</b> Services are matched by
/// <see cref="ServiceSpec.Name"/> using <see cref="StringComparison.Ordinal"/>
/// — supervisord program names are case-sensitive identifiers and so are
/// runtime spec entries by extension.</para>
///
/// <para><b>Removals.</b> Populated for completeness so the daemon can warn
/// + ignore (Phase 3 behaviour) or actually tear the supervisord program
/// down once that lands in a follow-up. The diff does NOT veto removal at
/// this layer — that's a daemon-side policy decision.</para>
///
/// <para><b>Install/Setup change detection.</b> Hash-based (SHA-256 of UTF-8
/// bytes). Null/empty/whitespace-equivalent strings collapse to the same
/// hash so cosmetic-only edits don't generate spurious deltas. When changed,
/// the new string is carried verbatim in <c>InstallNew</c> /
/// <c>SetupNew</c> so the daemon doesn't need a separate fetch round-trip.</para>
/// </summary>
public static class SpecDelta
{
    /// <summary>
    /// Compute the structural delta from <paramref name="current"/> →
    /// <paramref name="proposed"/>. Either side may be null (a fresh runtime
    /// has no current spec, an emptied spec proposes nothing). Null is
    /// treated as <c>new RuntimeSpecV2()</c> — no services, no install,
    /// no setup.
    /// </summary>
    public static RuntimeSpecDeltaV2 Compute(RuntimeSpecV2? current, RuntimeSpecV2? proposed)
    {
        var cur = current ?? new RuntimeSpecV2();
        var pro = proposed ?? new RuntimeSpecV2();

        var currentServices = (cur.Services ?? new List<ServiceSpec>())
            .ToDictionary(s => s.Name, s => s, StringComparer.Ordinal);
        var proposedServices = (pro.Services ?? new List<ServiceSpec>())
            .ToDictionary(s => s.Name, s => s, StringComparer.Ordinal);

        var newOrChanged = new List<ServiceSpec>();
        foreach (var proposedSvc in pro.Services ?? new List<ServiceSpec>())
        {
            if (!currentServices.TryGetValue(proposedSvc.Name, out var currentSvc))
            {
                // New service — not in current spec at all.
                newOrChanged.Add(proposedSvc);
                continue;
            }

            if (!ServicesEqual(currentSvc, proposedSvc))
            {
                // Same name, different shape — record the new desired state.
                newOrChanged.Add(proposedSvc);
            }
        }

        var removed = new List<string>();
        foreach (var name in currentServices.Keys)
        {
            if (!proposedServices.ContainsKey(name))
            {
                removed.Add(name);
            }
        }

        var installChanged = !HashEquals(cur.Install, pro.Install);
        var setupChanged = !HashEquals(cur.Setup, pro.Setup);

        return new RuntimeSpecDeltaV2
        {
            NewOrChangedServices = newOrChanged,
            RemovedServices = removed,
            InstallChanged = installChanged,
            InstallNew = installChanged ? pro.Install : null,
            SetupChanged = setupChanged,
            SetupNew = setupChanged ? pro.Setup : null,
        };
    }

    /// <summary>
    /// Convenience overload that parses both sides via
    /// <see cref="RuntimeSpecV2.TryParse"/>. Unparseable JSON on either side
    /// (e.g. a legacy V1 row that hasn't been migrated yet) collapses to an
    /// empty spec on that side — same defensive shape <see cref="GetProjectRuntimeSpecQueryHandler"/>
    /// uses for the read-side.
    /// </summary>
    public static RuntimeSpecDeltaV2 Compute(string? currentSpecJson, string? proposedSpecJson)
    {
        var current = ParseOrEmpty(currentSpecJson);
        var proposed = ParseOrEmpty(proposedSpecJson);
        return Compute(current, proposed);
    }

    /// <summary>
    /// Parse a spec JSON body into a <see cref="RuntimeSpecV2"/>, collapsing
    /// null / empty / unparseable input to an empty spec. Pulled out so
    /// readers (curation handlers, the runtime card query, the service-down
    /// detector) share one tolerance policy.
    /// </summary>
    public static RuntimeSpecV2 ParseOrEmpty(string? specJson)
    {
        if (string.IsNullOrWhiteSpace(specJson))
        {
            return new RuntimeSpecV2();
        }

        var parsed = RuntimeSpecV2.TryParse(specJson);
        return parsed.IsSuccess ? parsed.Value : new RuntimeSpecV2();
    }

    /// <summary>
    /// Whether two <see cref="ServiceSpec"/>s describe the same supervisord
    /// program. Compares every field; null and empty env dicts collapse to
    /// the same shape (a service without env reads the same as a service
    /// with an empty env block).
    /// </summary>
    private static bool ServicesEqual(ServiceSpec a, ServiceSpec b)
    {
        if (!string.Equals(a.Name, b.Name, StringComparison.Ordinal)) return false;
        if (!string.Equals(a.Command, b.Command, StringComparison.Ordinal)) return false;
        if (!string.Equals(a.User, b.User, StringComparison.Ordinal)) return false;
        if (a.Autorestart != b.Autorestart) return false;
        if (!string.Equals(a.Install, b.Install, StringComparison.Ordinal)) return false;
        // Verify is part of the service's contract — a verify-only edit must
        // propagate to the daemon so the next boot's skip path uses the new
        // predicate. We piggyback on the newOrChangedServices bucket
        // (same shape as a command-only edit) rather than inventing a new
        // bucket; the daemon will reload spec.installVerify on next boot.
        if (!string.Equals(a.InstallVerify, b.InstallVerify, StringComparison.Ordinal)) return false;
        if (!EnvEqual(a.Env, b.Env)) return false;
        if (!HealthcheckEqual(a.Healthcheck, b.Healthcheck)) return false;
        return true;
    }

    private static bool EnvEqual(Dictionary<string, string>? a, Dictionary<string, string>? b)
    {
        var aCount = a?.Count ?? 0;
        var bCount = b?.Count ?? 0;
        if (aCount == 0 && bCount == 0) return true;
        if (aCount != bCount) return false;
        foreach (var kvp in a!)
        {
            if (!b!.TryGetValue(kvp.Key, out var bVal)) return false;
            if (!string.Equals(kvp.Value, bVal, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static bool HealthcheckEqual(HealthcheckSpec? a, HealthcheckSpec? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return string.Equals(a.Command, b.Command, StringComparison.Ordinal)
               && a.IntervalSeconds == b.IntervalSeconds;
    }

    /// <summary>
    /// Whether two install/setup strings are semantically equal — normalises
    /// null and whitespace-only to the same bucket so a cosmetic edit
    /// doesn't trigger a needless re-run. Uses SHA-256 of UTF-8 bytes per
    /// the card spec; for two strings this is overkill versus a direct
    /// ordinal compare, but keeping the hash in place documents the
    /// "content-addressed" intent that lines up with the daemon's
    /// install-hash cache (Phase 2 work in the runtime-spec-v2 spec).
    /// </summary>
    private static bool HashEquals(string? a, string? b)
    {
        var aNorm = string.IsNullOrWhiteSpace(a) ? string.Empty : a;
        var bNorm = string.IsNullOrWhiteSpace(b) ? string.Empty : b;
        var hashA = SHA256.HashData(Encoding.UTF8.GetBytes(aNorm));
        var hashB = SHA256.HashData(Encoding.UTF8.GetBytes(bNorm));
        return hashA.AsSpan().SequenceEqual(hashB);
    }
}

/// <summary>
/// Structural diff between two <see cref="RuntimeSpecV2"/> documents. Drives
/// the daemon-bound <c>ApplyRuntimeSpecDelta</c> push: the daemon receives
/// only what changed, not the whole spec.
///
/// <para><see cref="HasChanges"/> is the cheap gate — when false, the
/// curation handlers still record the proposal decision but can skip the
/// SignalR push entirely if they want to.</para>
/// </summary>
[TranspilationSource]
public record RuntimeSpecDeltaV2
{
    /// <summary>
    /// Services that are new (not in the current spec) OR whose shape
    /// changed (same <see cref="ServiceSpec.Name"/>, different
    /// command/user/env/etc.). The daemon registers or restarts each one.
    /// </summary>
    public required List<ServiceSpec> NewOrChangedServices { get; init; }

    /// <summary>
    /// Names of services that exist in the current spec but not in the
    /// proposed one. Phase 3 daemons may warn-and-ignore; later phases will
    /// actually tear the supervisord program down. The diff layer doesn't
    /// take a position — it just reports.
    /// </summary>
    public required List<string> RemovedServices { get; init; }

    /// <summary>
    /// True when the top-level <see cref="RuntimeSpecV2.Install"/> string
    /// hashes differently between current and proposed (null / whitespace
    /// collapse to the same bucket). When true, <see cref="InstallNew"/>
    /// carries the new value.
    /// </summary>
    public required bool InstallChanged { get; init; }

    /// <summary>
    /// The new top-level install string when <see cref="InstallChanged"/>
    /// is true. Null when unchanged (saves wire bytes — the daemon already
    /// has the current value cached). May still be null when changed if the
    /// new spec clears the field.
    /// </summary>
    public string? InstallNew { get; init; }

    /// <summary>
    /// True when the top-level <see cref="RuntimeSpecV2.Setup"/> string
    /// hashes differently between current and proposed. Same null /
    /// whitespace normalisation as install.
    /// </summary>
    public required bool SetupChanged { get; init; }

    /// <summary>The new setup string when <see cref="SetupChanged"/> is true. Same null-when-unchanged convention as <see cref="InstallNew"/>.</summary>
    public string? SetupNew { get; init; }

    /// <summary>Computed: any field above is non-empty / true. Cheap pre-push gate.</summary>
    public bool HasChanges
        => NewOrChangedServices.Count > 0
           || RemovedServices.Count > 0
           || InstallChanged
           || SetupChanged;
}

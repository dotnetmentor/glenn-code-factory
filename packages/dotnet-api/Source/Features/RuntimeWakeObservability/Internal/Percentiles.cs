namespace Source.Features.RuntimeWakeObservability.Internal;

/// <summary>
/// Shared percentile arithmetic for the three RuntimeWakeObservability handlers.
/// Uses the <b>nearest-rank</b> method (NIST C=1 variant) — the value at the
/// 1-based position <c>ceil(p/100 * n)</c> in the sorted sample. Cheap, has no
/// interpolation surprises, and is the right shape for ops triage where the
/// raw observed wake duration matters more than a continuous estimate.
/// </summary>
public static class Percentiles
{
    /// <summary>
    /// Compute p50 and p95 over <paramref name="values"/>. Returns
    /// <c>(0, 0)</c> when the input is empty — handlers report empty stages
    /// rather than failing.
    /// </summary>
    /// <remarks>
    /// The caller may pass an unsorted list. We sort in place to avoid the
    /// allocation of a copy; callers that need to reuse the input should pass
    /// their own copy.
    /// </remarks>
    public static (long P50, long P95) NearestRank(List<long> values)
    {
        if (values.Count == 0)
        {
            return (0L, 0L);
        }

        values.Sort();

        return (
            P50: ValueAt(values, 50),
            P95: ValueAt(values, 95));
    }

    private static long ValueAt(IReadOnlyList<long> sorted, int percentile)
    {
        // Nearest-rank: 1-based rank = ceil(p/100 * n). Clamp into [1, n].
        var n = sorted.Count;
        var rank = (int)Math.Ceiling(percentile / 100.0 * n);
        if (rank < 1) rank = 1;
        if (rank > n) rank = n;
        return sorted[rank - 1];
    }
}

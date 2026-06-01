namespace Source.Features.RuntimeLifecycle.Models;

/// <summary>
/// Detail-view shape for <c>GET /api/admin/runtimes/{id}</c>. Combines the full
/// <see cref="ProjectRuntime"/> row with the most recent 50 lifecycle transitions so
/// operators can debug a stuck/failed runtime from a single response without paging.
///
/// <para>50 is the sweet spot empirically: enough history to see a crash-loop or
/// suspend-wake oscillation, small enough that the JSON stays under a few KB even when
/// metadata blobs are inlined.</para>
/// </summary>
public record RuntimeDetailResponse(
    ProjectRuntime Runtime,
    List<RuntimeTransitionDto> RecentTransitions);

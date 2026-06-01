namespace Source.Features.Cloudflare.Models;

/// <summary>
/// Lifecycle states of a <see cref="SubdomainAssignment"/>. The lifecycle is
/// strictly one-way and destroy-and-never-reuse:
///
/// <list type="bullet">
///   <item><see cref="Available"/> — freshly provisioned, sitting in the pool
///         waiting to be claimed by a branch.</item>
///   <item><see cref="Assigned"/> — atomically claimed by exactly one
///         <see cref="ProjectBranch"/>. Once here a row never returns to
///         <see cref="Available"/>.</item>
///   <item><see cref="Releasing"/> — the owning branch has been deleted; the
///         row is awaiting Cloudflare-side teardown (Phase 4). After teardown
///         the row is destroyed, not recycled.</item>
/// </list>
///
/// <para>Stored as a string in the database (see DbContext config) so log /
/// telemetry readers don't have to know enum ordinals to make sense of the
/// data.</para>
/// </summary>
public enum SubdomainStatus
{
    Available = 0,
    Assigned = 1,
    Releasing = 2,
}

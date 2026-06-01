using Tapper;

namespace Source.Features.Specifications.Models;

/// <summary>
/// Lifecycle bucket for a <see cref="Specification"/>. Persisted as <c>int</c> so
/// adding new entries later doesn't shift existing rows.
///
/// <para>Today only <see cref="Draft"/> exists — the platform-planning-kanban
/// spec is explicit that there is no <c>Accepted</c> / <c>Approved</c> state and
/// no approval workflow. Acceptance is conversational ("looks good, go" in
/// chat), not a state-machine transition. The enum is kept (rather than a
/// boolean) so future statuses can land without a column-type migration.</para>
/// </summary>
[TranspilationSource]
public enum SpecificationStatus
{
    Draft = 0,
}

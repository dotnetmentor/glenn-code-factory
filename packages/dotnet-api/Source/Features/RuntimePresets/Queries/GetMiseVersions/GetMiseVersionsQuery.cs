using Source.Features.RuntimePresets.Dtos;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimePresets.Queries.GetMiseVersions;

/// <summary>
/// Lookup the list of mise-installable versions for a single tool. Backs the
/// "Lookup versions" affordance in the super-admin preset editor — the operator
/// clicks the button next to a <c>miseTool</c>-flagged parameter and the UI
/// populates the version dropdown from the response.
///
/// <para>Pure pass-through to <see cref="Services.IMiseVersionLookup"/>; lives
/// as a query so the controller stays uniform (mediator-only) and so test
/// callers can swap the lookup at the DI layer.</para>
/// </summary>
public sealed record GetMiseVersionsQuery(string Tool)
    : IQuery<Result<MiseVersionsResponse>>;

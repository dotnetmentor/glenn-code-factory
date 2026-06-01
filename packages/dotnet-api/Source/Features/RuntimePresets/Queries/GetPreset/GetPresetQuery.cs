using Source.Features.RuntimePresets.Dtos;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimePresets.Queries.GetPreset;

/// <summary>
/// Fetch one <see cref="Models.ServicePreset"/> by id with the deserialised
/// env / parameters payload — backs the super-admin edit screen and the live
/// preview pane.
///
/// <para>Returns <c>preset_not_found</c> when the id is missing or
/// soft-deleted; the admin path doesn't expose archived rows (the spec defines
/// soft-delete as a tombstone, not an archive bin).</para>
/// </summary>
public sealed record GetPresetQuery(Guid Id)
    : IQuery<Result<ServicePresetDto>>;

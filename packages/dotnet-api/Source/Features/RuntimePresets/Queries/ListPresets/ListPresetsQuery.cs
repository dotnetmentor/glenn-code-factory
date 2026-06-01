using Source.Features.RuntimePresets.Dtos;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimePresets.Queries.ListPresets;

/// <summary>
/// List every non-soft-deleted <see cref="Models.ServicePreset"/> for the super
/// admin gallery. Returns built-in + user-cloned rows together — the UI flips
/// the edit / clone affordances based on
/// <see cref="Dtos.ServicePresetDto.IsBuiltIn"/>.
///
/// <para>Ordered by category (Backend, Frontend, Database, Worker, Other) then
/// display name, matching <c>IX_ServicePresets_Category_DisplayName</c> so the
/// query is an index scan.</para>
/// </summary>
public sealed record ListPresetsQuery : IQuery<Result<List<ServicePresetDto>>>;

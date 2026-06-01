using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimePresets.Dtos;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimePresets.Queries.ListPresets;

/// <summary>
/// Handler for <see cref="ListPresetsQuery"/>. Tracking-free read of every
/// non-soft-deleted preset, materialised in memory before the
/// <see cref="PresetDtoMapper.ToDto"/> projection because the mapping
/// deserialises the jsonb <c>EnvTemplate</c> / <c>Parameters</c> columns —
/// EF can't translate <c>JsonSerializer.Deserialize</c> server-side.
/// </summary>
public sealed class ListPresetsHandler
    : IQueryHandler<ListPresetsQuery, Result<List<ServicePresetDto>>>
{
    private readonly ApplicationDbContext _db;

    public ListPresetsHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<List<ServicePresetDto>>> Handle(
        ListPresetsQuery request,
        CancellationToken cancellationToken)
    {
        var rows = await _db.ServicePresets
            .AsNoTracking()
            .OrderBy(p => p.Category)
            .ThenBy(p => p.DisplayName)
            .ToListAsync(cancellationToken);

        var dtos = rows.Select(PresetDtoMapper.ToDto).ToList();
        return Result.Success(dtos);
    }
}

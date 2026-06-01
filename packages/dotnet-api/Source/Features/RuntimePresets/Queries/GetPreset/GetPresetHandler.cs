using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimePresets.Dtos;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimePresets.Queries.GetPreset;

/// <summary>
/// Handler for <see cref="GetPresetQuery"/>. Single tracking-free lookup; the
/// global soft-delete query filter hides tombstoned rows.
/// </summary>
public sealed class GetPresetHandler
    : IQueryHandler<GetPresetQuery, Result<ServicePresetDto>>
{
    public const string NotFoundError = "preset_not_found";

    private readonly ApplicationDbContext _db;

    public GetPresetHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<ServicePresetDto>> Handle(
        GetPresetQuery request,
        CancellationToken cancellationToken)
    {
        var entity = await _db.ServicePresets
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == request.Id, cancellationToken);

        if (entity is null)
        {
            return Result.Failure<ServicePresetDto>(NotFoundError);
        }

        return Result.Success(PresetDtoMapper.ToDto(entity));
    }
}

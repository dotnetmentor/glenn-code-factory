using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeImages.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimeImages.Queries.GetActiveRuntimeImage;

public sealed class GetActiveRuntimeImageHandler
    : IQueryHandler<GetActiveRuntimeImageQuery, Result<RuntimeImage?>>
{
    private readonly ApplicationDbContext _db;

    public GetActiveRuntimeImageHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<RuntimeImage?>> Handle(
        GetActiveRuntimeImageQuery request,
        CancellationToken cancellationToken)
    {
        var image = await _db.RuntimeImages
            .AsNoTracking()
            .Where(i => i.Status == RuntimeImageStatus.Active)
            .OrderByDescending(i => i.BuiltAt)
            .FirstOrDefaultAsync(cancellationToken);

        return Result.Success(image);
    }
}

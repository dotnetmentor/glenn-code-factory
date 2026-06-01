using Microsoft.EntityFrameworkCore;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimePresets.Commands.DeletePreset;

/// <summary>
/// Handler for <see cref="DeletePresetCommand"/>. Loads (honouring the global
/// soft-delete filter so double-delete returns 404), rejects built-ins, flips
/// the tombstone flag. <c>DeletedAt</c> / <c>DeletedBy</c> are stamped by
/// <see cref="ApplicationDbContext.SaveChangesAsync(CancellationToken)"/>.
/// </summary>
public sealed class DeletePresetHandler
    : ICommandHandler<DeletePresetCommand, Result>
{
    public const string NotFoundError = "preset_not_found";
    public const string BuiltInError = "preset_built_in";

    private readonly ApplicationDbContext _db;

    public DeletePresetHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result> Handle(
        DeletePresetCommand request,
        CancellationToken cancellationToken)
    {
        var entity = await _db.ServicePresets
            .FirstOrDefaultAsync(p => p.Id == request.Id, cancellationToken);
        if (entity is null)
        {
            return Result.Failure(NotFoundError);
        }

        if (entity.IsBuiltIn)
        {
            return Result.Failure(BuiltInError);
        }

        entity.IsDeleted = true;
        // DeletedAt / DeletedBy auto-stamped by SaveChangesAsync (ISoftDelete).
        await _db.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}

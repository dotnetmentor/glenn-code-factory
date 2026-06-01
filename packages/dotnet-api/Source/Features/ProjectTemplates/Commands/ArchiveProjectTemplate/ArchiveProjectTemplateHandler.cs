using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Source.Features.Users.Models;
using Source.Infrastructure;
using Source.Infrastructure.AuthorizationModels;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.ProjectTemplates.Commands.ArchiveProjectTemplate;

/// <summary>
/// Handler for <see cref="ArchiveProjectTemplateCommand"/>. Loads the row,
/// flips <see cref="Models.ProjectTemplate.IsDeleted"/>, lets
/// <see cref="ApplicationDbContext.SaveChangesAsync"/> stamp the audit columns.
/// Idempotent — already-archived or missing rows return success.
/// </summary>
public sealed class ArchiveProjectTemplateHandler
    : ICommandHandler<ArchiveProjectTemplateCommand, Result>
{
    public const string NotAuthorizedError = "not_authorized";

    private readonly ApplicationDbContext _db;
    private readonly UserManager<User> _userManager;

    public ArchiveProjectTemplateHandler(ApplicationDbContext db, UserManager<User> userManager)
    {
        _db = db;
        _userManager = userManager;
    }

    public async Task<Result> Handle(
        ArchiveProjectTemplateCommand request,
        CancellationToken cancellationToken)
    {
        // Defence-in-depth — controller already gates on SuperAdmin.
        if (string.IsNullOrWhiteSpace(request.CallerUserId))
        {
            return Result.Failure(NotAuthorizedError);
        }
        var user = await _userManager.FindByIdAsync(request.CallerUserId);
        if (user is null)
        {
            return Result.Failure(NotAuthorizedError);
        }
        var isSuperAdmin = await _userManager.IsInRoleAsync(user, RoleConstants.SuperAdmin);
        if (!isSuperAdmin)
        {
            return Result.Failure(NotAuthorizedError);
        }

        // Lift the soft-delete filter so a double-archive call hits the same
        // row and returns success rather than 404. The DELETE contract is
        // idempotent — see the command summary.
        var entity = await _db.ProjectTemplates
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == request.TemplateId, cancellationToken);

        if (entity is null)
        {
            // Row never existed — still idempotent success per the contract.
            return Result.Success();
        }

        if (entity.IsDeleted)
        {
            // Already archived — no-op.
            return Result.Success();
        }

        entity.IsDeleted = true;
        // DeletedAt / DeletedBy auto-stamped by ApplicationDbContext.SaveChangesAsync (ISoftDelete).
        await _db.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}

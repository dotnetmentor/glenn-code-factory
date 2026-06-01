using Microsoft.EntityFrameworkCore;
using Source.Features.Workspaces.Services;
using Source.Infrastructure;
using Source.Infrastructure.Workspaces;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Workspaces.Commands;

/// <summary>
/// Rename and/or re-slug the workspace currently in <see cref="IWorkspaceContext"/>.
/// Pass <c>null</c> for properties you do not want to change.
/// </summary>
public record RenameWorkspaceCommand(string? Name, string? Slug) : ICommand<Result<RenameWorkspaceResponse>>;

public record RenameWorkspaceResponse
{
    public required Guid Id { get; init; }
    public required string Slug { get; init; }
    public required string Name { get; init; }
}

public sealed class RenameWorkspaceHandler : ICommandHandler<RenameWorkspaceCommand, Result<RenameWorkspaceResponse>>
{
    private readonly ApplicationDbContext _db;
    private readonly IWorkspaceContext _wsCtx;
    private readonly IWorkspaceSlugGenerator _slugGen;

    public RenameWorkspaceHandler(ApplicationDbContext db, IWorkspaceContext wsCtx, IWorkspaceSlugGenerator slugGen)
    {
        _db = db;
        _wsCtx = wsCtx;
        _slugGen = slugGen;
    }

    public async Task<Result<RenameWorkspaceResponse>> Handle(RenameWorkspaceCommand request, CancellationToken cancellationToken)
    {
        var workspace = await _db.Workspaces.SingleOrDefaultAsync(w => w.Id == _wsCtx.Id, cancellationToken);
        if (workspace is null) return Result.Failure<RenameWorkspaceResponse>("Workspace not found");

        var newName = !string.IsNullOrWhiteSpace(request.Name) ? request.Name.Trim() : workspace.Name;

        // If slug changes, ensure global uniqueness via the generator.
        var newSlug = workspace.Slug;
        if (!string.IsNullOrWhiteSpace(request.Slug) && request.Slug.Trim().ToLowerInvariant() != workspace.Slug)
        {
            newSlug = await _slugGen.GenerateAsync(request.Slug, cancellationToken);
        }

        var renameResult = workspace.Rename(newName, newSlug);
        if (renameResult.IsFailure) return Result.Failure<RenameWorkspaceResponse>(renameResult.Error!);

        await _db.SaveChangesAsync(cancellationToken);

        return Result.Success(new RenameWorkspaceResponse
        {
            Id = workspace.Id,
            Slug = workspace.Slug,
            Name = workspace.Name,
        });
    }
}

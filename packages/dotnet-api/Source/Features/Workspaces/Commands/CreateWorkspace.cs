using Microsoft.EntityFrameworkCore;
using Source.Features.WorkspaceSpecs.Services;
using Source.Features.Workspaces.Models;
using Source.Features.Workspaces.Services;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Workspaces.Commands;

/// <summary>
/// Create a new workspace owned by <paramref name="OwnerUserId"/>.
/// If <paramref name="Slug"/> is provided we use it (with collision suffix on conflict);
/// otherwise the slug is generated from <paramref name="SlugSeed"/> (or <paramref name="Name"/>).
/// </summary>
public record CreateWorkspaceCommand(
    string OwnerUserId,
    string Name,
    string? Slug,
    string? SlugSeed
) : ICommand<Result<CreateWorkspaceResponse>>;

public record CreateWorkspaceResponse
{
    public required Guid Id { get; init; }
    public required string Slug { get; init; }
    public required string Name { get; init; }
}

public sealed class CreateWorkspaceHandler : ICommandHandler<CreateWorkspaceCommand, Result<CreateWorkspaceResponse>>
{
    private readonly ApplicationDbContext _db;
    private readonly IWorkspaceSlugGenerator _slugGenerator;

    public CreateWorkspaceHandler(ApplicationDbContext db, IWorkspaceSlugGenerator slugGenerator)
    {
        _db = db;
        _slugGenerator = slugGenerator;
    }

    public async Task<Result<CreateWorkspaceResponse>> Handle(CreateWorkspaceCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.OwnerUserId))
            return Result.Failure<CreateWorkspaceResponse>("Owner is required");
        if (string.IsNullOrWhiteSpace(request.Name))
            return Result.Failure<CreateWorkspaceResponse>("Name is required");

        var owner = await _db.Users.FindAsync([request.OwnerUserId], cancellationToken);
        if (owner is null)
            return Result.Failure<CreateWorkspaceResponse>("Owner user does not exist");

        // Resolve slug.
        string slug;
        if (!string.IsNullOrWhiteSpace(request.Slug))
        {
            // User supplied an explicit slug — sanitize it then ensure uniqueness via the generator.
            slug = await _slugGenerator.GenerateAsync(request.Slug, cancellationToken);
        }
        else
        {
            var seed = !string.IsNullOrWhiteSpace(request.SlugSeed) ? request.SlugSeed! : request.Name;
            slug = await _slugGenerator.GenerateAsync(seed, cancellationToken);
        }

        var workspace = new Workspace
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            Name = request.Name.Trim(),
            OwnerId = request.OwnerUserId,
        };

        var membership = new WorkspaceMembership
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            UserId = request.OwnerUserId,
            Role = WorkspaceRole.Owner,
        };

        _db.Workspaces.Add(workspace);
        _db.WorkspaceMemberships.Add(membership);

        // Seed the workspace's catalog with the system-curated starter specs
        // (Scene 6 of workspace-spec-catalog). Owner of the workspace is the
        // natural CreatedBy / UpdatedBy for these seeds. StarterCatalogSpecs
        // validates every entry against the V2 RuntimeSpec validator and
        // throws if any starter is malformed — better to crash workspace
        // creation than to silently ship bad seeds into every new workspace.
        _db.WorkspaceSpecs.AddRange(
            StarterCatalogSpecs.BuildFor(workspace.Id, request.OwnerUserId));

        workspace.MarkCreated();

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Slug collided despite our generator (extremely rare race) — bubble a friendly error.
            return Result.Failure<CreateWorkspaceResponse>("Workspace slug is already taken — please retry");
        }

        return Result.Success(new CreateWorkspaceResponse
        {
            Id = workspace.Id,
            Slug = workspace.Slug,
            Name = workspace.Name,
        });
    }
}

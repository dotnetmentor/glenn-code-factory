using Microsoft.EntityFrameworkCore;
using Source.Features.GitHub.Services;
using Source.Features.GitHub.Services.Dtos;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.GitHub.Commands;

/// <summary>
/// Handles the GitHub <c>installation_repositories</c> webhook. Comes with two actions:
///   <c>added</c>   — upsert each entry in <c>repositories_added</c>.
///   <c>removed</c> — delete each entry in <c>repositories_removed</c> by GitHub repo id.
/// Both paths reuse <see cref="IGithubRepositorySyncService"/> so the upsert/delete logic
/// stays in one place.
/// </summary>
public record HandleGithubInstallationRepositoriesEventCommand(
    GithubInstallationRepositoriesWebhookPayload Payload
) : ICommand<Result>;

public sealed class HandleGithubInstallationRepositoriesEventHandler
    : ICommandHandler<HandleGithubInstallationRepositoriesEventCommand, Result>
{
    private readonly ApplicationDbContext _db;
    private readonly IGithubRepositorySyncService _sync;
    private readonly ILogger<HandleGithubInstallationRepositoriesEventHandler> _logger;

    public HandleGithubInstallationRepositoriesEventHandler(
        ApplicationDbContext db,
        IGithubRepositorySyncService sync,
        ILogger<HandleGithubInstallationRepositoriesEventHandler> logger)
    {
        _db = db;
        _sync = sync;
        _logger = logger;
    }

    public async Task<Result> Handle(
        HandleGithubInstallationRepositoriesEventCommand request,
        CancellationToken cancellationToken)
    {
        var payload = request.Payload;
        var action = payload.Action?.ToLowerInvariant();
        var installation = payload.Installation;
        if (installation is null || installation.Id <= 0)
        {
            _logger.LogWarning("installation_repositories webhook missing installation.id; action={Action}", action);
            return Result.Success();
        }

        var localInstallation = await _db.GithubInstallations
            .SingleOrDefaultAsync(i => i.InstallationId == installation.Id, cancellationToken);
        if (localInstallation is null)
        {
            // Either the install callback hasn't landed yet (race) or this install belongs to a
            // workspace we don't track. Either way we have nothing to attach repos to.
            _logger.LogInformation(
                "installation_repositories webhook for unknown installation_id={InstallationId}; ignoring.",
                installation.Id);
            return Result.Success();
        }

        switch (action)
        {
            case "added":
                if (payload.RepositoriesAdded is { Count: > 0 } added)
                {
                    await _sync.UpsertFromWebhookAsync(localInstallation.Id, added, cancellationToken);
                }
                return Result.Success();

            case "removed":
                if (payload.RepositoriesRemoved is { Count: > 0 } removed)
                {
                    await _sync.RemoveByGithubRepoIdsAsync(
                        localInstallation.Id,
                        removed.Select(r => r.Id),
                        cancellationToken);
                }
                return Result.Success();

            default:
                _logger.LogInformation(
                    "installation_repositories webhook with unrecognised action {Action}; ignoring.", action);
                return Result.Success();
        }
    }
}

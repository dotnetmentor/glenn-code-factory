using Microsoft.EntityFrameworkCore;
using Source.Features.GitHub.Services.Dtos;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.GitHub.Commands;

/// <summary>
/// Handles the GitHub <c>installation</c> webhook event. Drives the lifecycle of a
/// <see cref="Source.Features.GitHub.Models.GithubInstallation"/> row in response to:
///   <c>created</c> — informational; the canonical create path is the install callback.
///                    If we already know about the installation we'll opportunistically
///                    refresh the metadata; if not (no workspace context here) we just log.
///   <c>deleted</c>  — hard-remove the row and cascade its repos.
///   <c>suspend</c>  — flip <c>Suspended = true</c>.
///   <c>unsuspend</c>— flip <c>Suspended = false</c>.
///   anything else — logged + ignored, returns success so we ack the webhook.
/// </summary>
public record HandleGithubInstallationEventCommand(
    GithubInstallationWebhookPayload Payload
) : ICommand<Result>;

public sealed class HandleGithubInstallationEventHandler
    : ICommandHandler<HandleGithubInstallationEventCommand, Result>
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<HandleGithubInstallationEventHandler> _logger;

    public HandleGithubInstallationEventHandler(
        ApplicationDbContext db,
        ILogger<HandleGithubInstallationEventHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Result> Handle(HandleGithubInstallationEventCommand request, CancellationToken cancellationToken)
    {
        var payload = request.Payload;
        var action = payload.Action?.ToLowerInvariant();
        var installation = payload.Installation;

        if (installation is null || installation.Id <= 0)
        {
            _logger.LogWarning("installation webhook missing installation.id; action={Action}", action);
            return Result.Success();
        }

        var existing = await _db.GithubInstallations
            .SingleOrDefaultAsync(i => i.InstallationId == installation.Id, cancellationToken);

        switch (action)
        {
            case "created":
                if (existing is null)
                {
                    // Normal flow: the install callback already created the row before this webhook
                    // fires. If we've never heard of this installation_id we don't have a workspace
                    // to attach it to — log and ack.
                    _logger.LogInformation(
                        "installation.created webhook for unknown installation_id={InstallationId}; ignoring (no workspace context).",
                        installation.Id);
                    return Result.Success();
                }
                // Idempotent refresh of mutable account metadata.
                if (installation.Account is { } account)
                {
                    existing.AccountLogin = account.Login;
                    existing.AccountType = account.Type;
                    existing.AccountAvatarUrl = account.AvatarUrl;
                }
                existing.Suspended = installation.SuspendedAt.HasValue;
                await _db.SaveChangesAsync(cancellationToken);
                return Result.Success();

            case "deleted":
                if (existing is null)
                {
                    _logger.LogInformation(
                        "installation.deleted webhook for unknown installation_id={InstallationId}; nothing to remove.",
                        installation.Id);
                    return Result.Success();
                }
                var repos = await _db.GithubRepositories
                    .Where(r => r.GithubInstallationId == existing.Id)
                    .ToListAsync(cancellationToken);
                if (repos.Count > 0)
                {
                    _db.GithubRepositories.RemoveRange(repos);
                }
                _db.GithubInstallations.Remove(existing);
                await _db.SaveChangesAsync(cancellationToken);
                _logger.LogInformation(
                    "installation.deleted: removed installation {InstallationId} and {RepoCount} repos.",
                    installation.Id, repos.Count);
                return Result.Success();

            case "suspend":
                if (existing is null)
                {
                    _logger.LogInformation("installation.suspend for unknown installation_id={InstallationId}; ignoring.",
                        installation.Id);
                    return Result.Success();
                }
                existing.Suspended = true;
                await _db.SaveChangesAsync(cancellationToken);
                return Result.Success();

            case "unsuspend":
                if (existing is null)
                {
                    _logger.LogInformation("installation.unsuspend for unknown installation_id={InstallationId}; ignoring.",
                        installation.Id);
                    return Result.Success();
                }
                existing.Suspended = false;
                await _db.SaveChangesAsync(cancellationToken);
                return Result.Success();

            default:
                _logger.LogInformation("installation webhook with unrecognised action {Action}; ignoring.", action);
                return Result.Success();
        }
    }
}

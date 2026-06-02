using Microsoft.EntityFrameworkCore;
using Source.Features.Projects.Services;
using Source.Features.ProjectSecrets.Services;
using Source.Features.SignalR.Services;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Commands.UpdateProjectByok;

public sealed class UpdateProjectByokHandler
    : ICommandHandler<UpdateProjectByokCommand, Result<UpdateProjectByokResponse>>
{
    public const string NotFoundPrefix = "not-found:";
    public const string ProjectOverrideDisabled = "project_cursor_key_not_allowed";

    private readonly ApplicationDbContext _db;
    private readonly SecretEncryptionService _encryption;
    private readonly ICursorApiKeyResolver _cursorKeys;
    private readonly ILogger<UpdateProjectByokHandler> _logger;

    public UpdateProjectByokHandler(
        ApplicationDbContext db,
        SecretEncryptionService encryption,
        ICursorApiKeyResolver cursorKeys,
        ILogger<UpdateProjectByokHandler> logger)
    {
        _db = db;
        _encryption = encryption;
        _cursorKeys = cursorKeys;
        _logger = logger;
    }

    public async Task<Result<UpdateProjectByokResponse>> Handle(
        UpdateProjectByokCommand request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CallingUserId))
        {
            return Result.Failure<UpdateProjectByokResponse>(
                $"{NotFoundPrefix} unauthenticated");
        }

        var project = await _db.Projects
            .Include(p => p.Workspace)
            .FirstOrDefaultAsync(p => p.Id == request.ProjectId, cancellationToken);

        if (project is null || project.OwnerUserId != request.CallingUserId)
        {
            return Result.Failure<UpdateProjectByokResponse>(
                $"{NotFoundPrefix} project {request.ProjectId} not found");
        }

        if (request.CursorApiKey.IsSet
            && request.CursorApiKey.Value is not null
            && !project.Workspace.AllowProjectCursorApiKeyOverride)
        {
            return Result.Failure<UpdateProjectByokResponse>(ProjectOverrideDisabled);
        }

        if (request.CursorApiKey.IsSet)
        {
            var cursorError = await ApplySecretAsync(
                request.CursorApiKey,
                request.ProjectId,
                value => project.EncryptedCursorApiKey = value,
                cancellationToken);
            if (cursorError is not null)
            {
                return Result.Failure<UpdateProjectByokResponse>(cursorError);
            }

            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "UpdateProjectByok: project {ProjectId} updated by {UserId}. cursorChanged={CursorChanged} (now hasCursor={HasCursor}).",
                request.ProjectId,
                request.CallingUserId,
                request.CursorApiKey.IsSet,
                project.EncryptedCursorApiKey is not null);
        }

        var status = await _cursorKeys.GetStatusForProjectAsync(request.ProjectId, cancellationToken);

        return Result.Success(new UpdateProjectByokResponse(
            ProjectId: project.Id,
            HasCursorApiKey: status.HasProjectCursorApiKey,
            HasWorkspaceCursorApiKey: status.HasWorkspaceCursorApiKey,
            AllowProjectCursorApiKeyOverride: status.AllowProjectCursorApiKeyOverride,
            HasEffectiveCursorApiKey: status.HasEffectiveCursorApiKey));
    }

    private async Task<string?> ApplySecretAsync(
        OptionalSecret secret,
        Guid projectId,
        Action<string?> setter,
        CancellationToken ct)
    {
        if (!secret.IsSet)
        {
            return null;
        }

        if (secret.Value is null)
        {
            setter(null);
            return null;
        }

        var plaintext = secret.Value;
        if (string.IsNullOrEmpty(plaintext))
        {
            return "empty_secret";
        }

        var (ciphertext, nonce, dekVersion) = await _encryption.EncryptAsync(
            projectId, plaintext, ct);
        var envelope = ProjectByokEnvelope.Pack(ciphertext, nonce, dekVersion);
        setter(envelope);
        return null;
    }
}

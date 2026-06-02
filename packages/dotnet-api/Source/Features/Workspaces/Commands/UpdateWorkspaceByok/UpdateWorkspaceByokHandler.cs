using Microsoft.EntityFrameworkCore;
using Source.Features.Projects.Commands.UpdateProjectByok;
using Source.Features.Projects.Services;
using Source.Features.ProjectSecrets.Services;
using Source.Infrastructure;
using Source.Features.Workspaces.Models;
using Source.Infrastructure.Workspaces;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Workspaces.Commands.UpdateWorkspaceByok;

public sealed class UpdateWorkspaceByokHandler
    : ICommandHandler<UpdateWorkspaceByokCommand, Result<UpdateWorkspaceByokResponse>>
{
    private readonly ApplicationDbContext _db;
    private readonly IWorkspaceContext _wsCtx;
    private readonly SecretEncryptionService _encryption;
    private readonly ILogger<UpdateWorkspaceByokHandler> _logger;

    public UpdateWorkspaceByokHandler(
        ApplicationDbContext db,
        IWorkspaceContext wsCtx,
        SecretEncryptionService encryption,
        ILogger<UpdateWorkspaceByokHandler> logger)
    {
        _db = db;
        _wsCtx = wsCtx;
        _encryption = encryption;
        _logger = logger;
    }

    public async Task<Result<UpdateWorkspaceByokResponse>> Handle(
        UpdateWorkspaceByokCommand request,
        CancellationToken cancellationToken)
    {
        if (_wsCtx.Role is not (WorkspaceRole.Owner or WorkspaceRole.Admin))
        {
            return Result.Failure<UpdateWorkspaceByokResponse>("forbidden");
        }

        var workspace = await _db.Workspaces
            .FirstOrDefaultAsync(w => w.Id == _wsCtx.Id, cancellationToken);

        if (workspace is null)
        {
            return Result.Failure<UpdateWorkspaceByokResponse>("not_found");
        }

        if (request.CursorApiKey.IsSet)
        {
            var cursorError = await ApplyCursorKeyAsync(
                workspace.Id,
                request.CursorApiKey,
                envelope => workspace.SetEncryptedCursorApiKey(envelope),
                cancellationToken);
            if (cursorError is not null)
            {
                return Result.Failure<UpdateWorkspaceByokResponse>(cursorError);
            }
        }

        if (request.SetAllowProjectCursorApiKeyOverride
            && request.AllowProjectCursorApiKeyOverride.HasValue)
        {
            workspace.SetAllowProjectCursorApiKeyOverride(
                request.AllowProjectCursorApiKeyOverride.Value);
        }

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "UpdateWorkspaceByok: workspace {WorkspaceId} updated by {UserId}. hasCursor={HasCursor} allowProjectOverride={AllowOverride}.",
            workspace.Id,
            _wsCtx.UserId,
            workspace.EncryptedCursorApiKey is not null,
            workspace.AllowProjectCursorApiKeyOverride);

        return Result.Success(new UpdateWorkspaceByokResponse(
            WorkspaceId: workspace.Id,
            HasCursorApiKey: workspace.EncryptedCursorApiKey is not null,
            AllowProjectCursorApiKeyOverride: workspace.AllowProjectCursorApiKeyOverride));
    }

    private async Task<string?> ApplyCursorKeyAsync(
        Guid workspaceId,
        OptionalSecret secret,
        Func<string?, Result> setter,
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

        if (string.IsNullOrEmpty(secret.Value))
        {
            return "empty_secret";
        }

        var (ciphertext, nonce, dekVersion) = await _encryption.EncryptForWorkspaceAsync(
            workspaceId, secret.Value, ct);
        var envelope = ProjectByokEnvelope.Pack(ciphertext, nonce, dekVersion);
        var result = setter(envelope);
        return result.IsFailure ? result.Error : null;
    }
}

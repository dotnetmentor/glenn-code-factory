using MediatR;
using Microsoft.EntityFrameworkCore;
using Source.Features.ProjectSecrets.Events;
using Source.Features.ProjectSecrets.Models;
using Source.Features.ProjectSecrets.Services;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.ProjectSecrets.Commands;

/// <summary>
/// Rotate the value of an existing project secret. Re-encrypts the plaintext
/// under the project's DEK, updates ciphertext/nonce/DEK version, bumps
/// <see cref="ProjectSecret.Version"/>, writes a
/// <see cref="SecretAuditAction.Updated"/> audit row, and publishes
/// <see cref="SecretsChanged"/> after commit.
///
/// <para><b>Not-found:</b> a missing or soft-deleted row returns
/// <c>"not_found"</c>; the controller maps that to 404. Soft-deletion is
/// excluded by the global query filter on <see cref="ProjectSecret"/>.</para>
///
/// <para><b>Version bump:</b> always +1 on successful update — the daemon
/// bootstrap handshake uses the version to detect changes without comparing
/// ciphertext bytes (Card 4 / 5).</para>
/// </summary>
public record UpdateSecretCommand(
    Guid ProjectId,
    string Key,
    string Plaintext,
    string ActorUserId,
    Guid? BranchId = null,
    bool IsSecret = true) : ICommand<Result<UpdateSecretResponse>>;

/// <summary>
/// Result of <see cref="UpdateSecretCommand"/>: the secret's id and the new
/// version number (post-bump).
/// </summary>
public record UpdateSecretResponse(Guid SecretId, int Version);

public class UpdateSecretCommandHandler : ICommandHandler<UpdateSecretCommand, Result<UpdateSecretResponse>>
{
    private readonly ApplicationDbContext _db;
    private readonly SecretEncryptionService _encryption;
    private readonly IMediator _mediator;

    public UpdateSecretCommandHandler(
        ApplicationDbContext db,
        SecretEncryptionService encryption,
        IMediator mediator)
    {
        _db = db;
        _encryption = encryption;
        _mediator = mediator;
    }

    public async Task<Result<UpdateSecretResponse>> Handle(
        UpdateSecretCommand request,
        CancellationToken cancellationToken)
    {
        var keyError = SecretValidation.ValidateKey(request.Key);
        if (keyError is not null)
        {
            return Result.Failure<UpdateSecretResponse>(keyError);
        }

        var plaintextError = SecretValidation.ValidatePlaintext(request.Plaintext);
        if (plaintextError is not null)
        {
            return Result.Failure<UpdateSecretResponse>(plaintextError);
        }

        // Default query filter excludes soft-deleted rows — a deleted-then-
        // re-added key has a fresh row and an Update against the deleted one
        // is a not_found, which is the right user-facing answer.
        var existing = await _db.ProjectSecrets
            .FirstOrDefaultAsync(
                s => s.ProjectId == request.ProjectId
                  && s.BranchId == request.BranchId
                  && s.Key == request.Key,
                cancellationToken);

        if (existing is null)
        {
            return Result.Failure<UpdateSecretResponse>("not_found");
        }

        var (ciphertext, nonce, dekVersion) = await _encryption.EncryptAsync(
            request.ProjectId, request.Plaintext, cancellationToken);

        existing.Ciphertext = ciphertext;
        existing.Nonce = nonce;
        existing.DekVersion = dekVersion;
        existing.IsSecret = request.IsSecret;
        existing.Version += 1;

        _db.SecretAuditEvents.Add(new SecretAuditEvent
        {
            Id = Guid.NewGuid(),
            Action = SecretAuditAction.Updated,
            ProjectId = request.ProjectId,
            SecretId = existing.Id,
            SecretKey = existing.Key,
            Actor = request.ActorUserId,
            CreatedAt = DateTime.UtcNow,
        });

        await _db.SaveChangesAsync(cancellationToken);

        await _mediator.Publish(
            new SecretsChanged(request.ProjectId, request.Key, Deleted: false, request.BranchId),
            cancellationToken);

        return Result.Success(new UpdateSecretResponse(existing.Id, existing.Version));
    }
}

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
/// Add a new secret (env-var) to a project. Encrypts the plaintext under the
/// project's DEK, inserts the <see cref="ProjectSecret"/> row, writes a
/// <see cref="SecretAuditAction.Created"/> audit row in the same transaction,
/// and (after commit) publishes <see cref="SecretsChanged"/> so the Card 4
/// handler can push the delta to running daemons.
///
/// <list type="bullet">
///   <item><b>Validation:</b> <see cref="Key"/> must match
///         <c>^[A-Z][A-Z0-9_]*$</c> and be 1..200 chars. <see cref="Plaintext"/>
///         must not contain <c>'\n'</c> — the daemon's env-file format doesn't
///         quote, so a newline would cut the value silently.</item>
///   <item><b>Conflict:</b> the unique partial index on (ProjectId, Key)
///         where DeletedAt IS NULL surfaces as a
///         <see cref="DbUpdateException"/>; we translate that to
///         <c>"key_already_exists"</c>.</item>
///   <item><b>Plaintext lifetime:</b> the encryption service zeroes the DEK
///         after each call. The plaintext string itself is held only on the
///         stack of this method — we don't log it, don't retain it, don't
///         echo it back in <see cref="AddSecretResponse"/>.</item>
/// </list>
/// </summary>
public record AddSecretCommand(
    Guid ProjectId,
    string Key,
    string Plaintext,
    string ActorUserId,
    Guid? BranchId = null,
    bool IsSecret = true) : ICommand<Result<AddSecretResponse>>;

/// <summary>
/// Result of <see cref="AddSecretCommand"/>: the new secret's id and version
/// (always 1 on a fresh insert; bumped on update). The plaintext is never
/// echoed back.
/// </summary>
public record AddSecretResponse(Guid SecretId, int Version);

public class AddSecretCommandHandler : ICommandHandler<AddSecretCommand, Result<AddSecretResponse>>
{
    private readonly ApplicationDbContext _db;
    private readonly SecretEncryptionService _encryption;
    private readonly IMediator _mediator;
    private readonly ILogger<AddSecretCommandHandler> _logger;

    public AddSecretCommandHandler(
        ApplicationDbContext db,
        SecretEncryptionService encryption,
        IMediator mediator,
        ILogger<AddSecretCommandHandler> logger)
    {
        _db = db;
        _encryption = encryption;
        _mediator = mediator;
        _logger = logger;
    }

    public async Task<Result<AddSecretResponse>> Handle(
        AddSecretCommand request,
        CancellationToken cancellationToken)
    {
        var keyError = SecretValidation.ValidateKey(request.Key);
        if (keyError is not null)
        {
            return Result.Failure<AddSecretResponse>(keyError);
        }

        var plaintextError = SecretValidation.ValidatePlaintext(request.Plaintext);
        if (plaintextError is not null)
        {
            return Result.Failure<AddSecretResponse>(plaintextError);
        }

        var (ciphertext, nonce, dekVersion) = await _encryption.EncryptAsync(
            request.ProjectId, request.Plaintext, cancellationToken);

        var secret = new ProjectSecret
        {
            Id = Guid.NewGuid(),
            ProjectId = request.ProjectId,
            BranchId = request.BranchId,
            Key = request.Key,
            Ciphertext = ciphertext,
            Nonce = nonce,
            DekVersion = dekVersion,
            Version = 1,
            IsSecret = request.IsSecret,
            CreatedBy = request.ActorUserId,
        };
        _db.ProjectSecrets.Add(secret);

        // Audit row in the SAME transaction. Even if SaveChanges throws on the
        // unique-key violation below we want the row attached so the change is
        // atomic with the secret insert. Action = Created.
        _db.SecretAuditEvents.Add(new SecretAuditEvent
        {
            Id = Guid.NewGuid(),
            Action = SecretAuditAction.Created,
            ProjectId = request.ProjectId,
            SecretId = secret.Id,
            SecretKey = request.Key,
            Actor = request.ActorUserId,
            CreatedAt = DateTime.UtcNow,
        });

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            _logger.LogInformation(
                "AddSecret: key_already_exists for project {ProjectId} key {Key}",
                request.ProjectId, request.Key);
            return Result.Failure<AddSecretResponse>("key_already_exists");
        }

        // Publish AFTER commit. The Card 4 handler is the consumer; failures in
        // the handler must not roll back the persisted secret.
        await _mediator.Publish(
            new SecretsChanged(request.ProjectId, request.Key, Deleted: false, request.BranchId),
            cancellationToken);

        return Result.Success(new AddSecretResponse(secret.Id, secret.Version));
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        // Postgres surfaces unique-violation as SQLSTATE 23505. The Npgsql
        // exception's SqlState property carries the code; we walk the chain
        // because EF wraps the original PostgresException. We don't reference
        // Npgsql.PostgresException directly to keep this handler provider-
        // agnostic — the InMemory provider used in tests has no concept of
        // unique-index enforcement, so this branch is exercised at the Postgres
        // integration level.
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            var sqlState = current.GetType().GetProperty("SqlState")?.GetValue(current) as string;
            if (sqlState == "23505") return true;
        }
        return false;
    }
}

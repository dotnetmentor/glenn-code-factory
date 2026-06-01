using Microsoft.EntityFrameworkCore;
using Source.Features.ProjectSecrets.Models;
using Source.Features.ProjectSecrets.Services;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.ProjectSecrets.Queries;

/// <summary>
/// Decrypt and return the plaintext value of a single project secret. The
/// audit row (<see cref="SecretAuditAction.Revealed"/>) is committed
/// <b>before</b> decryption so the trail is preserved even if the AEAD path
/// throws after the point of no return — and so a fast-followup compromise
/// of the audit table would still show the access.
///
/// <para><b>Plaintext lifetime.</b> Held only in the response on the way out
/// of this handler. The encryption service zeroes its DEK + intermediate
/// plaintext bytes; the only remaining copy is the immutable string returned
/// to the controller, which is serialised once on the response and never
/// retained by this slice. Callers (Card 4 / 5 / frontend) MUST NOT log
/// the response body.</para>
///
/// <para><b>Why a query, not a command.</b> The IRequest split in this
/// codebase doesn't track read-vs-write at the protocol level — this is
/// classified as a query because callers expect "give me the value" semantics
/// and the controller maps it to <c>GET /reveal</c>. The audit-row write is
/// a side effect of the read, not the primary action.</para>
/// </summary>
public record RevealSecretQuery(
    Guid ProjectId,
    string Key,
    string ActorUserId) : IQuery<Result<RevealSecretResponse>>;

/// <summary>
/// Result of <see cref="RevealSecretQuery"/>: the env-var name and its
/// decrypted value. Returned exactly once; callers must not retain.
/// </summary>
public record RevealSecretResponse(string Key, string Plaintext);

public class RevealSecretQueryHandler : IQueryHandler<RevealSecretQuery, Result<RevealSecretResponse>>
{
    private readonly ApplicationDbContext _db;
    private readonly SecretEncryptionService _encryption;

    public RevealSecretQueryHandler(
        ApplicationDbContext db,
        SecretEncryptionService encryption)
    {
        _db = db;
        _encryption = encryption;
    }

    public async Task<Result<RevealSecretResponse>> Handle(
        RevealSecretQuery request,
        CancellationToken cancellationToken)
    {
        // Default query filter excludes soft-deleted rows — a revealed
        // never-existed key and a revealed soft-deleted key both surface as
        // not_found, which is the right answer (no row, no value).
        var secret = await _db.ProjectSecrets
            .FirstOrDefaultAsync(
                s => s.ProjectId == request.ProjectId && s.Key == request.Key,
                cancellationToken);

        if (secret is null)
        {
            return Result.Failure<RevealSecretResponse>("not_found");
        }

        // Audit row is committed BEFORE decryption. Two reasons:
        //   1) If decryption throws (corrupted nonce, missing DEK on rotation)
        //      we still want the access attempt on record.
        //   2) Mirrors the security pattern used by analogous "reveal" flows
        //      elsewhere (RuntimeTokenService.MintAsync writes its issue row
        //      before returning the JWT).
        _db.SecretAuditEvents.Add(new SecretAuditEvent
        {
            Id = Guid.NewGuid(),
            Action = SecretAuditAction.Revealed,
            ProjectId = request.ProjectId,
            SecretId = secret.Id,
            SecretKey = secret.Key,
            Actor = request.ActorUserId,
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(cancellationToken);

        var plaintext = await _encryption.DecryptAsync(
            request.ProjectId,
            secret.Ciphertext,
            secret.Nonce,
            secret.DekVersion,
            cancellationToken);

        return Result.Success(new RevealSecretResponse(secret.Key, plaintext));
    }
}

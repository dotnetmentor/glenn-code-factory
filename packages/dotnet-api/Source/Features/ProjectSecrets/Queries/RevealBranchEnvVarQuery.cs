using Microsoft.EntityFrameworkCore;
using Source.Features.ProjectSecrets.Models;
using Source.Features.ProjectSecrets.Services;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.ProjectSecrets.Queries;

/// <summary>
/// Branch-effective variant of <see cref="RevealSecretQuery"/>: decrypt and
/// return the plaintext for a single env var <em>as the branch sees it</em>. If
/// this branch has its own row for the key, that branch-specific value is
/// revealed; otherwise we fall back to the project-wide (<c>BranchId == null</c>)
/// row. Missing at both scopes → <c>not_found</c>.
///
/// <para><b>Audit.</b> Writes a <see cref="SecretAuditAction.Revealed"/> row
/// BEFORE decryption — same security ordering as <see cref="RevealSecretQuery"/>:
/// the access is on record even if the AEAD path throws after the point of no
/// return. The audit row records the resolved secret's id / key, so a branch
/// reveal that fell back to a project-wide row is attributed to that row.</para>
///
/// <para><b>Plaintext lifetime.</b> Returned exactly once on the response wire;
/// callers must not log or retain it. See <see cref="RevealSecretQuery"/> for the
/// full lifetime note.</para>
/// </summary>
public record RevealBranchEnvVarQuery(
    Guid ProjectId,
    Guid BranchId,
    string Key,
    string ActorUserId) : IQuery<Result<RevealSecretResponse>>;

public class RevealBranchEnvVarQueryHandler
    : IQueryHandler<RevealBranchEnvVarQuery, Result<RevealSecretResponse>>
{
    private readonly ApplicationDbContext _db;
    private readonly SecretEncryptionService _encryption;

    public RevealBranchEnvVarQueryHandler(
        ApplicationDbContext db,
        SecretEncryptionService encryption)
    {
        _db = db;
        _encryption = encryption;
    }

    public async Task<Result<RevealSecretResponse>> Handle(
        RevealBranchEnvVarQuery request,
        CancellationToken cancellationToken)
    {
        // Pull both candidate rows (branch-specific + project-wide) for this key,
        // then resolve branch-effective: branch wins, else fall back to the
        // project-wide row. Soft-deleted rows are excluded by the global filter.
        var candidates = await _db.ProjectSecrets
            .Where(s => s.ProjectId == request.ProjectId
                && s.Key == request.Key
                && (s.BranchId == null || s.BranchId == request.BranchId))
            .ToListAsync(cancellationToken);

        ProjectSecret? secret =
            candidates.FirstOrDefault(s => s.BranchId == request.BranchId)
            ?? candidates.FirstOrDefault(s => s.BranchId == null);

        if (secret is null)
        {
            return Result.Failure<RevealSecretResponse>("not_found");
        }

        // Audit committed BEFORE decryption — same ordering / reasoning as
        // RevealSecretQuery (access on record even if decrypt throws).
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

using Microsoft.EntityFrameworkCore;
using Source.Features.ProjectSecrets.Models;
using Source.Features.Users.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.ProjectSecrets.Queries;

/// <summary>
/// Enumerate the metadata for a project's secrets — keys + version + audit
/// timestamps + creator display name. <b>Never</b> returns plaintext, ciphertext,
/// or nonce.
///
/// <para><b>No audit row.</b> Listing is high-volume (every page load on the
/// secrets admin UI hits this) and the answer leaks zero secret material;
/// polluting the audit trail with one row per page render would drown the
/// security-relevant signals (Reveal, CrossTenantDenied) in noise. The
/// product spec calls for audit on writes + reveals only.</para>
///
/// <para><b>Soft-deleted rows are excluded</b> by the global query filter on
/// <see cref="ProjectSecret"/>.</para>
/// </summary>
public record ListSecretsQuery(Guid ProjectId) : IQuery<Result<List<SecretMetadataDto>>>;

/// <summary>
/// Metadata projection of a <see cref="ProjectSecret"/> for the admin list view.
/// Plaintext / ciphertext / nonce / DEK version are deliberately absent — this
/// DTO is safe to log, ship to the frontend, and serialise into Swagger.
/// <see cref="CreatedByUserName"/> is null when the FK doesn't resolve (e.g.
/// system-seeded rows or a deleted user).
/// </summary>
public record SecretMetadataDto(
    Guid Id,
    string Key,
    int Version,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? CreatedByUserName);

public class ListSecretsQueryHandler : IQueryHandler<ListSecretsQuery, Result<List<SecretMetadataDto>>>
{
    private readonly ApplicationDbContext _db;

    public ListSecretsQueryHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<List<SecretMetadataDto>>> Handle(
        ListSecretsQuery request,
        CancellationToken cancellationToken)
    {
        // GroupJoin against the User table so a missing / deleted creator
        // doesn't drop the secret row (left-join semantics). Identity user ids
        // are strings — the FK on ProjectSecret.CreatedBy is nullable, and we
        // fall back to UserName then Email for display.
        var rows = await (
            from secret in _db.ProjectSecrets
            where secret.ProjectId == request.ProjectId && secret.BranchId == null
            join u in _db.Users on secret.CreatedBy equals u.Id into users
            from user in users.DefaultIfEmpty()
            orderby secret.Key
            select new SecretMetadataDto(
                secret.Id,
                secret.Key,
                secret.Version,
                secret.CreatedAt,
                secret.UpdatedAt,
                user != null ? (user.UserName ?? user.Email) : null))
            .ToListAsync(cancellationToken);

        return Result.Success(rows);
    }
}

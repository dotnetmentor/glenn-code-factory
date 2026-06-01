using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeTokens.Services;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimeTokens.Commands;

/// <summary>
/// Revokes a single RuntimeToken JWT by its <c>jti</c>. Idempotent — a second
/// revocation of the same token is a no-op (the first revocation wins for the
/// audit trail). Already-expired tokens are also a no-op: JWT lifetime
/// validation would reject them anyway, so we don't write to the DB or prime
/// the cache.
///
/// <para>On success, the in-memory <see cref="IRevocationCache"/> is primed
/// with the jti so the very next ValidateAsync rejects the token without any
/// further DB hit.</para>
/// </summary>
public record RevokeTokenCommand(Guid Jti, string Reason) : ICommand<Result>;

public class RevokeTokenCommandHandler : ICommandHandler<RevokeTokenCommand, Result>
{
    /// <summary>Mirrors the HasMaxLength(256) on RuntimeTokenIssue.RevocationReason.</summary>
    private const int ReasonMaxLength = 256;

    private readonly ApplicationDbContext _db;
    private readonly IRevocationCache _revocationCache;

    public RevokeTokenCommandHandler(ApplicationDbContext db, IRevocationCache revocationCache)
    {
        _db = db;
        _revocationCache = revocationCache;
    }

    public async Task<Result> Handle(RevokeTokenCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return Result.Failure("revocation_reason_required");
        }

        var trimmedReason = request.Reason.Trim();
        if (trimmedReason.Length > ReasonMaxLength)
        {
            // Silently clamp — over-long reason is operator UX, not a programming
            // bug worth surfacing as an error. The column is HasMaxLength(256).
            trimmedReason = trimmedReason.Substring(0, ReasonMaxLength);
        }

        // RuntimeTokenIssue has no soft-delete / IAuditable filter, so a plain
        // FirstOrDefaultAsync on the PK is correct — no IgnoreQueryFilters needed.
        var row = await _db.RuntimeTokenIssues
            .FirstOrDefaultAsync(r => r.Id == request.Jti, cancellationToken);

        if (row is null)
        {
            return Result.Failure("token_not_found");
        }

        // Already-expired: the JWT lifetime check rejects the token regardless,
        // and IRevocationCache.Revoke skips already-expired calls per its own
        // contract. Cleanest: don't write anything, return success.
        if (row.ExpiresAt <= DateTime.UtcNow)
        {
            return Result.Success();
        }

        // Already-revoked: idempotent. Don't overwrite RevokedAt/Reason — the
        // first revocation wins for the audit trail.
        if (row.RevokedAt is not null)
        {
            return Result.Success();
        }

        row.RevokedAt = DateTime.UtcNow;
        row.RevocationReason = trimmedReason;

        await _db.SaveChangesAsync(cancellationToken);

        // Order matters: only prime the cache after the DB write succeeded.
        // If SaveChangesAsync threw, we would NOT want a stale in-memory entry
        // that doesn't match a row on disk.
        _revocationCache.Revoke(row.Id, row.ExpiresAt);

        return Result.Success();
    }
}

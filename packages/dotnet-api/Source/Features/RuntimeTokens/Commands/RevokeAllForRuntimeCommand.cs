using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeTokens.Services;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimeTokens.Commands;

/// <summary>
/// Revokes every alive (non-revoked, non-expired) RuntimeToken JWT for a given
/// runtime. Returns the number of rows actually revoked.
///
/// <para>Cardinality is bounded — there's typically one alive token per runtime
/// at a time (issuance overwrites the previous one logically, even though the
/// audit row remains). The loop + single SaveChanges shape is deliberate: it
/// keeps change tracking active so we can prime the cache for each newly-revoked
/// jti after persistence; ExecuteUpdateAsync would bypass change tracking AND
/// behave differently on the in-memory test provider.</para>
/// </summary>
public record RevokeAllForRuntimeCommand(Guid RuntimeId, string Reason) : ICommand<Result<int>>;

public class RevokeAllForRuntimeCommandHandler : ICommandHandler<RevokeAllForRuntimeCommand, Result<int>>
{
    private const int ReasonMaxLength = 256;

    private readonly ApplicationDbContext _db;
    private readonly IRevocationCache _revocationCache;

    public RevokeAllForRuntimeCommandHandler(ApplicationDbContext db, IRevocationCache revocationCache)
    {
        _db = db;
        _revocationCache = revocationCache;
    }

    public async Task<Result<int>> Handle(RevokeAllForRuntimeCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return Result.Failure<int>("revocation_reason_required");
        }

        var trimmedReason = request.Reason.Trim();
        if (trimmedReason.Length > ReasonMaxLength)
        {
            trimmedReason = trimmedReason.Substring(0, ReasonMaxLength);
        }

        var now = DateTime.UtcNow;

        var rows = await _db.RuntimeTokenIssues
            .Where(r => r.RuntimeId == request.RuntimeId
                && r.RevokedAt == null
                && r.ExpiresAt > now)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return Result.Success(0);
        }

        foreach (var row in rows)
        {
            row.RevokedAt = now;
            row.RevocationReason = trimmedReason;
        }

        await _db.SaveChangesAsync(cancellationToken);

        // Only prime the cache for rows we ourselves updated. Pre-revoked rows
        // were filtered out above, so we don't redundantly re-prime them.
        foreach (var row in rows)
        {
            _revocationCache.Revoke(row.Id, row.ExpiresAt);
        }

        return Result.Success(rows.Count);
    }
}

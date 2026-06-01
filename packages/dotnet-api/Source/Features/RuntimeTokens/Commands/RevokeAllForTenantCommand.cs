using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeTokens.Services;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimeTokens.Commands;

/// <summary>
/// Revokes every alive (non-revoked, non-expired) RuntimeToken JWT belonging to
/// a given tenant. Returns the number of rows actually revoked.
///
/// <para><b>There is no Tenant entity today;</b> this command is a query over
/// the <c>RuntimeTokenIssue.TenantId</c> column. When tenancy lands as its own
/// slice, the entity migration may or may not need to wire here — the column-
/// level filter will keep working until the schema actually changes shape.</para>
///
/// <para>Note: rows with <c>TenantId = null</c> are NOT matched even if the
/// caller passes a Guid that happens to equal Guid.Empty — null is null in EF.</para>
/// </summary>
public record RevokeAllForTenantCommand(Guid TenantId, string Reason) : ICommand<Result<int>>;

public class RevokeAllForTenantCommandHandler : ICommandHandler<RevokeAllForTenantCommand, Result<int>>
{
    private const int ReasonMaxLength = 256;

    private readonly ApplicationDbContext _db;
    private readonly IRevocationCache _revocationCache;

    public RevokeAllForTenantCommandHandler(ApplicationDbContext db, IRevocationCache revocationCache)
    {
        _db = db;
        _revocationCache = revocationCache;
    }

    public async Task<Result<int>> Handle(RevokeAllForTenantCommand request, CancellationToken cancellationToken)
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
            .Where(r => r.TenantId == request.TenantId
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

        foreach (var row in rows)
        {
            _revocationCache.Revoke(row.Id, row.ExpiresAt);
        }

        return Result.Success(rows.Count);
    }
}

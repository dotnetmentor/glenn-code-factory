namespace Source.Features.RuntimeTokens.Models;

/// <summary>
/// Body shape for every revoke endpoint. Reason is mandatory — every revocation
/// has to leave a forensic trail. The handler validates non-empty + trims +
/// truncates to 256 chars (matches the column constraint).
/// </summary>
public record RevokeRequest(string Reason);

/// <summary>Single-token revoke result. The 200 vs 404 distinction is handled in the controller.</summary>
public record RevokeTokenResponse(Guid Jti);

/// <summary>Bulk revoke result — count of rows actually flipped to revoked.</summary>
public record BulkRevokeResponse(int RevokedCount);

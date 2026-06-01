namespace Source.Features.FlyManagement.Models;

/// <summary>
/// Diagnostic payload returned by <c>POST /api/admin/fly/test-connection</c>. Reports
/// presence of every <c>Fly:*</c> SystemSettings key, then exercises the configured
/// PAT + app name via <see cref="FlyClient.PingAsync"/> (which GETs <c>/apps/{appName}</c>).
///
/// <para>The endpoint always returns 200 with this body even on auth failure — the UI
/// reads the structured fields. <see cref="IsValid"/> drives the overall verdict;
/// <see cref="Message"/> is a one-liner summary for the operator.</para>
///
/// <para><see cref="PingSucceeded"/> tracks whether <see cref="FlyClient.PingAsync"/>
/// returned <c>true</c> at all (200 or 404 — both prove auth + transport). The narrower
/// <see cref="AppExists"/> is reserved for when we later distinguish 200 from 404 — for
/// now it mirrors <see cref="PingSucceeded"/> since <c>PingAsync</c> collapses both into
/// a single boolean.</para>
/// </summary>
public sealed record FlyTestConnectionResponse(
    bool ApiTokenSet,
    bool AppNameSet,
    bool OrgSlugSet,
    bool PingSucceeded,
    string? PingError,
    bool AppExists,
    string? AppName,
    bool IsValid,
    string Message);

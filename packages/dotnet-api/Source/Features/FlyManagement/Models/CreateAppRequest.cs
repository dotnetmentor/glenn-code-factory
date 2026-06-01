namespace Source.Features.FlyManagement.Models;

/// <summary>
/// Body of <c>POST /v1/apps</c>. Used by <c>FlyClient.EnsureAppAsync</c> when the
/// configured Fly app doesn't yet exist — the typical first-boot bootstrap path.
///
/// <para>Snake-case wire shape (<c>app_name</c>, <c>org_slug</c>) is handled by the
/// FlyClient's shared <c>JsonNamingPolicy.SnakeCaseLower</c> serialiser settings — no
/// per-property annotations required.</para>
/// </summary>
public record CreateAppRequest(string AppName, string OrgSlug);

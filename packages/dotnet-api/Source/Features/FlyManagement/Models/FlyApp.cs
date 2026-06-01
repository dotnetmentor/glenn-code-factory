namespace Source.Features.FlyManagement.Models;

/// <summary>
/// Subset of the Fly app resource (<c>GET /v1/apps/{name}</c>) we surface to callers.
/// Apps are the top-level namespace under which every machine and volume lives — Card 6
/// adds the GET/EnsureApp pair so a fresh deploy can self-bootstrap before Card 3+5 try
/// to create machines or volumes against a name that doesn't exist yet.
///
/// <para>As with <see cref="FlyMachine"/> and <see cref="FlyVolume"/>, snake-case wire
/// fields (<c>org_slug</c>, <c>created_at</c>) are mapped via the FlyClient's shared
/// <c>JsonNamingPolicy.SnakeCaseLower</c> serialiser. <see cref="Status"/> is stringly
/// typed for the same reason as the volume/machine state — Fly evolves the lifecycle
/// vocabulary and we don't want a closed enum to brittle-fail on a new value.</para>
/// </summary>
public record FlyApp(
    string Id,
    string Name,
    string OrgSlug,
    string Status,
    DateTime CreatedAt);

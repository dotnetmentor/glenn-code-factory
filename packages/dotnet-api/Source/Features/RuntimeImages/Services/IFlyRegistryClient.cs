namespace Source.Features.RuntimeImages.Services;

/// <summary>
/// Thin OCI v2 HTTP client for the Fly registry (<c>https://registry.fly.io</c>). Used
/// by the super-admin UI to discover what tags have been pushed for a given image so a
/// human can pick which one to register and activate — a deliberate move away from the
/// old "CI auto-registers on push" flow where the build pipeline made the activation
/// decision unilaterally.
///
/// <para>The interface stays intentionally minimal: <see cref="ListTagsAsync"/> for the
/// catalog and <see cref="GetManifestAsync"/> for per-tag metadata (digest, size, build
/// time, labels). The controller composes them into the
/// <c>GET /api/admin/runtime-images/registry-tags</c> response.</para>
///
/// <para><b>Auth.</b> Implementations must stamp <c>Authorization: Bearer {Fly:ApiToken}</c>
/// on every request — Fly's registry uses the same Personal Access Token as the Machines
/// API.</para>
/// </summary>
public interface IFlyRegistryClient
{
    /// <summary>
    /// List every tag that has been pushed under <paramref name="imageName"/>. Returns an
    /// empty list when the image exists but has no tags; throws
    /// <see cref="FlyRegistryException"/> on auth failure, network failure, or
    /// image-not-found (404).
    /// </summary>
    Task<List<string>> ListTagsAsync(string imageName, CancellationToken ct);

    /// <summary>
    /// Fetch the manifest for a single <paramref name="tag"/>. Combines the manifest
    /// response (digest + config size) and the config blob it points to (created
    /// timestamp + labels) into a single <see cref="RegistryManifestInfo"/>. Throws
    /// <see cref="FlyRegistryException"/> if the manifest or config blob cannot be
    /// retrieved.
    /// </summary>
    Task<RegistryManifestInfo> GetManifestAsync(string imageName, string tag, CancellationToken ct);
}

namespace Source.Features.RuntimeImages.Models;

/// <summary>
/// Request body for <c>POST /api/admin/runtime-images</c>. CI builds and pushes a new image,
/// then calls this endpoint with the metadata. Field semantics mirror <see cref="RuntimeImage"/>
/// — see that type for what each field means.
/// </summary>
public record RegisterRuntimeImageRequest(
    string Tag,
    string Digest,
    string Registry,
    string GitSha,
    DateTime BuiltAt,
    int SizeMb,
    string? Notes);

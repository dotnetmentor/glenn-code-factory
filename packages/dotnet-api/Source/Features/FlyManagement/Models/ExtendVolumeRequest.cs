namespace Source.Features.FlyManagement.Models;

/// <summary>
/// Body of <c>PUT /v1/apps/{app}/volumes/{id}/extend</c>. Volumes can grow but never
/// shrink — Fly enforces that on their side, we don't bother validating it twice here.
/// The single <c>size_gb</c> field is serialised snake-case via the FlyClient's shared
/// <c>JsonNamingPolicy.SnakeCaseLower</c> settings so the property name on the wire
/// matches Fly's expected schema.
/// </summary>
public record ExtendVolumeRequest(int SizeGb);

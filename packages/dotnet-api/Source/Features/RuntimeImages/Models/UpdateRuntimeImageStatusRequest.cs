namespace Source.Features.RuntimeImages.Models;

/// <summary>
/// Request body for <c>PATCH /api/admin/runtime-images/{id}/status</c>. The single field
/// is the desired lifecycle state — see <see cref="RuntimeImageStatus"/> for the closed
/// set of legal values. The controller deserialises with the global string-enum converter
/// so callers send <c>"Active"</c> / <c>"Deprecated"</c> / <c>"Yanked"</c> rather than
/// the underlying ordinals.
/// </summary>
public sealed record UpdateRuntimeImageStatusRequest(RuntimeImageStatus Status);

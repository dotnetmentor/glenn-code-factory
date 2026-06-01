namespace Source.Features.RuntimeImages.Models;

/// <summary>
/// Paged response shape for <c>GET /api/admin/runtime-images</c>.
/// <para><see cref="Items"/> carries the slice for the current page, ordered by
/// <see cref="RuntimeImage.BuiltAt"/> descending so the newest build lands first — operators
/// auditing the registry almost always want the most recent rows. <see cref="Total"/> is the
/// unpaged row count so the UI can render a "x of N" counter without a follow-up COUNT round trip.</para>
/// </summary>
public record RuntimeImagesResponse(
    List<RuntimeImage> Items,
    int Total,
    int Page,
    int PageSize);

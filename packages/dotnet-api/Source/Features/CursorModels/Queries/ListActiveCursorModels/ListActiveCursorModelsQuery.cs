using Source.Features.CursorModels.Models;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.CursorModels.Queries.ListActiveCursorModels;

/// <summary>
/// List every <em>active</em> <see cref="CursorModel"/> for the user-facing
/// picker. Backed by <c>GET /api/cursor-models/active</c> — open to any
/// authenticated user (not super-admin), since the project settings page
/// renders the Cursor model dropdown from it.
///
/// <para>Ordering mirrors the curated seed order via <c>DisplayName</c>
/// secondary sort, with a stable primary tie-breaker on <c>Slug</c> so two
/// rows that share a display name don't shuffle between requests.</para>
/// </summary>
public sealed record ListActiveCursorModelsQuery : IQuery<Result<List<CursorModelDto>>>;

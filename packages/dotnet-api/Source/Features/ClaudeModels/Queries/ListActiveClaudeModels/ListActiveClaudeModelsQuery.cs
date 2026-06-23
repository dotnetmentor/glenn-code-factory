using Source.Features.ClaudeModels.Models;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.ClaudeModels.Queries.ListActiveClaudeModels;

/// <summary>
/// List every <em>active</em> <see cref="ClaudeModel"/> for the user-facing
/// picker. Backed by <c>GET /api/claude-models/active</c> — open to any
/// authenticated user (not super-admin), since the composer renders the Claude
/// model dropdown from it when the conversation's agent backend is
/// <c>"claude"</c>.
///
/// <para>Ordering mirrors the curated seed order via <c>SortOrder</c>, with a
/// <c>DisplayName</c> secondary sort and a stable <c>Slug</c> tie-breaker so two
/// rows that share a display name don't shuffle between requests.</para>
/// </summary>
public sealed record ListActiveClaudeModelsQuery : IQuery<Result<List<ClaudeModelDto>>>;

using Source.Features.Specifications.Models;
using Tapper;

namespace Source.Features.Specifications.Queries;

/// <summary>
/// Full read-side projection of a <see cref="Specification"/>, including the
/// markdown body. Used by <see cref="ReadSpecificationQuery"/> and the MCP
/// read tool. Audit fields (<c>CreatedBy</c>, <c>DeletedBy</c>) are omitted —
/// the daemon never needs to know who wrote the spec and exposing identity
/// strings cross-tenant would be a leak.
///
/// <para><b>[TranspilationSource].</b> Tapper transpiles the record so the
/// daemon's generated TS layer has a matching type. Mirrors the
/// <see cref="Source.Features.ProjectKanban.Queries.ProjectKanbanCardDto"/>
/// precedent.</para>
/// </summary>
[TranspilationSource]
public record SpecificationDto(
    Guid Id,
    string Slug,
    string Name,
    string Content,
    SpecificationStatus Status,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>
/// Lean read-side projection used by <see cref="ListSpecificationsQuery"/>.
/// Deliberately drops <c>Content</c> so the list endpoint stays cheap even
/// when a project accumulates many long specs — clients hit the detail
/// endpoint when they actually need the body.
/// </summary>
[TranspilationSource]
public record SpecificationSummaryDto(
    Guid Id,
    string Slug,
    string Name,
    SpecificationStatus Status,
    DateTime CreatedAt,
    DateTime UpdatedAt);

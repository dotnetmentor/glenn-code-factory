namespace Source.Features.GitHub.Models;

/// <summary>
/// Controller-shape DTO for the live repo picker (<c>GET /api/github/installations/{id}/repos</c>).
/// Distinct from <see cref="Source.Features.GitHub.Services.Dtos.GithubRepoDto"/>, which mirrors
/// GitHub's wire format. This shape is what the frontend consumes — flat, only the fields the
/// repo-picker UI needs.
///
/// <para><see cref="LinkedProjectId"/> + <see cref="LinkedProjectName"/> are populated when the
/// caller passes a <c>workspaceId</c> query param: each repo row is cross-referenced against the
/// workspace's projects so the picker can render an "Open existing project" action without an
/// extra round-trip. Both are <c>null</c> when no workspace context is supplied (existing
/// callers) or when no live project links that repo on that installation.</para>
/// </summary>
public sealed record GithubRepoListItemDto(
    string Owner,
    string Name,
    string DefaultBranch,
    bool Private,
    Guid? LinkedProjectId = null,
    string? LinkedProjectName = null);

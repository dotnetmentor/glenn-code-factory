using System.ComponentModel.DataAnnotations;

namespace Source.Features.Projects.Models;

/// <summary>
/// Wire shape for <c>POST /api/projects</c>. Carries everything needed to spin
/// the onboarding atom: which workspace owns the project, which GitHub
/// installation it authenticates through, the repo coordinates and the default
/// branch name. The handler resolves the caller from the JWT and uses the
/// repo name as the project's display name (frontend can rename later).
/// </summary>
public class CreateProjectRequest
{
    /// <summary>Tenancy boundary — caller must be a member of this workspace.</summary>
    [Required]
    public required Guid WorkspaceId { get; init; }

    /// <summary>FK to the GitHub installation the project authenticates through.
    /// Must belong to <see cref="WorkspaceId"/>.</summary>
    [Required]
    public required Guid GithubInstallationId { get; init; }

    /// <summary>GitHub repo owner login (user or org). Ignored when
    /// <see cref="CreateNewRepo"/> is true (the handler fills it from the GitHub
    /// response).</summary>
    [StringLength(120)]
    public string? RepoOwner { get; init; }

    /// <summary>GitHub repo name. Doubles as the project's initial display name.
    /// Ignored when <see cref="CreateNewRepo"/> is true.</summary>
    [StringLength(120)]
    public string? RepoName { get; init; }

    /// <summary>Git branch the default <c>ProjectBranch</c> row is created with
    /// (e.g. <c>"main"</c>). Marked <c>IsDefault = true</c>. Ignored when
    /// <see cref="CreateNewRepo"/> is true (defaults to "main" on a fresh repo).</summary>
    [StringLength(250)]
    public string? BranchName { get; init; }

    /// <summary>
    /// "Brand new repo" mode. When true, the API creates a fresh empty repository
    /// on GitHub under the installation's account (auto-initialised with a README),
    /// and uses that repo as the project's repo. <see cref="NewRepoName"/> is
    /// required. <see cref="RepoOwner"/>/<see cref="RepoName"/>/<see cref="BranchName"/>
    /// are ignored.
    /// </summary>
    public bool CreateNewRepo { get; init; }

    /// <summary>
    /// Name for the brand-new repo. Required when <see cref="CreateNewRepo"/> is true.
    /// GitHub naming rules apply: 1..100 chars, alphanumerics/hyphens/underscores/periods.
    /// The handler trims and validates loosely (the strict check lives on GitHub).
    /// </summary>
    [StringLength(100, MinimumLength = 1)]
    public string? NewRepoName { get; init; }

    /// <summary>Optional description for the brand-new repo. Surfaced on the repo page.</summary>
    [StringLength(350)]
    public string? NewRepoDescription { get; init; }

    /// <summary>
    /// Optional dev-server preview port for the cloudflare-tunnel-preview
    /// feature. Omit to use the default (5173 — Vite). Must lie in
    /// 1..65535; the handler maps an out-of-range value to a 400 with
    /// <c>invalid_preview_port</c>.
    /// </summary>
    [Range(1, 65535)]
    public int? PreviewPort { get; init; }

    /// <summary>
    /// Optional starting spec picked from the workspace's spec catalog
    /// (workspace-spec-catalog Scene 3). When set, the catalog entry's content
    /// is deep-copied onto the new project's first <c>ProjectRuntime.Spec</c>
    /// at create time. The catalog entry must belong to <see cref="WorkspaceId"/>;
    /// a missing or cross-workspace id collapses to a 404. Omit (or send null)
    /// to start the project's runtime with a blank spec — the "Blank — no
    /// services" default in the new-project dialog.
    /// </summary>
    public Guid? CatalogSpecId { get; init; }

    /// <summary>
    /// Optional Starter (<see cref="Source.Features.ProjectTemplates.Models.ProjectTemplate"/>)
    /// to use as the seed for the new project. When set, the handler creates a
    /// fresh repo from the Starter's GitHub template repo (via the GitHub
    /// "generate from template" API) and snapshots the Starter's inline
    /// <c>RuntimeSpec</c> JSON onto the new project's first
    /// <c>ProjectRuntime.Spec</c>. Starters are GLOBAL — no workspace filter
    /// applies. A missing, inactive, or soft-deleted id collapses to a 404 with
    /// the <c>template_not_found</c> error code. When set, the
    /// <see cref="CreateNewRepo"/>/<see cref="RepoOwner"/>/<see cref="RepoName"/>/
    /// <see cref="BranchName"/> inputs are ignored — the template branch is
    /// authoritative for repo source. Omit (or send null) to preserve today's
    /// behaviour byte-for-byte.
    /// </summary>
    public Guid? TemplateId { get; init; }
}

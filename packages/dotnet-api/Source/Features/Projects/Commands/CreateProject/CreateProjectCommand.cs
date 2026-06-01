using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Commands.CreateProject;

/// <summary>
/// Onboarding atom for the e2e-smoketest spec — creates a <see cref="Models.Project"/>,
/// its default <see cref="Models.ProjectBranch"/> and a <c>Pending</c>
/// <see cref="Source.Features.RuntimeLifecycle.Models.ProjectRuntime"/> in a
/// single transaction. The recurring <c>RuntimeProvisionerJob</c> then walks
/// the runtime forward; this handler does NOT enqueue anything ad-hoc.
///
/// <para>Authorization is encoded in the handler:</para>
/// <list type="bullet">
///   <item>caller must be a member of <paramref name="WorkspaceId"/> — failure
///         maps to 403 in the controller;</item>
///   <item><paramref name="GithubInstallationId"/> must exist and belong to the
///         same workspace — failure maps to 400.</item>
/// </list>
///
/// <para><b>Return type.</b> The success value is a discriminated
/// <see cref="CreateProjectOutcome"/>: either the new <c>ProjectDto</c> (HTTP 200) or a
/// <see cref="RepositoryAlreadyLinkedConflict"/> payload when the (workspace, installation,
/// repo) coordinates already belong to a live project (HTTP 409). The duplicate-check is
/// part of the happy-path Result rather than the failure channel because the response
/// carries actionable structured data the frontend needs ("Open existing project ABC"),
/// not just an error code.</para>
/// </summary>
public record CreateProjectCommand(
    string CallerUserId,
    Guid WorkspaceId,
    Guid GithubInstallationId,
    // For "connect existing repo" mode: the owner/name/branch on GitHub. When
    // CreateNewRepo == true these are IGNORED — the handler creates a brand-new
    // repo under the installation account using NewRepoName + NewRepoDescription
    // and then proceeds with the canonical create flow using the created repo's
    // owner/name and "main" as the default branch.
    string RepoOwner,
    string RepoName,
    string BranchName,
    // Optional override for the project's dev-server preview port. Null means
    // "use the project default" (Vite's 5173). Validated against
    // 1..65535 by Project.SetPreviewPort.
    int? PreviewPort = null,
    // workspace-spec-catalog Scene 3. Optional starting spec picked from the
    // workspace catalog — its Content is deep-copied onto the new project's
    // first ProjectRuntime row (V2 RuntimeSpec). Null means "Blank — no
    // services" (the default for new projects, since there is no source
    // branch to carry from). Cross-workspace ids collapse to a
    // <see cref="CreateProjectHandler.CatalogSpecNotFoundError"/> sentinel
    // surfaced by the controller as a 404.
    Guid? CatalogSpecId = null,
    // "Brand new repo" mode. When true, the handler creates a fresh empty
    // repository on GitHub (under the installation's account) and uses that as
    // the project's repo. RepoOwner/RepoName/BranchName fields are overwritten
    // with the GitHub-side values after creation. NewRepoName is required when
    // CreateNewRepo == true.
    bool CreateNewRepo = false,
    string? NewRepoName = null,
    string? NewRepoDescription = null,
    // Starters (project-templates) — optional GLOBAL template id. When set, the
    // handler short-circuits the repo-source decision: it loads the template
    // (rejecting missing/archived/soft-deleted rows as <c>template_not_found</c>),
    // creates a fresh repo from the template's source repo via the GitHub
    // "generate from template" API, and snapshots the template's inline
    // RuntimeSpec JSON onto the new project's first ProjectRuntime.Spec. When
    // null, the existing CreateNewRepo / connect-existing-repo branches run
    // byte-identically to today. NewRepoName/NewRepoDescription are reused as
    // the new repo's name (falls back to the template Slug) and description.
    Guid? TemplateId = null
) : ICommand<Result<CreateProjectOutcome>>;

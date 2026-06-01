using Hangfire;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Source.Features.Cloudflare.Commands;
using Source.Features.FlyManagement.Configuration;
using Source.Features.GitHub.Models;
using Source.Features.GitHub.Services;
using Source.Features.GitHub.Services.Dtos;
using Source.Features.Projects.Models;
using Source.Features.RuntimeImages.Models;
using Source.Features.RuntimeLifecycle.Jobs;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.Workspaces.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Commands.CreateProject;

/// <summary>
/// Handles <see cref="CreateProjectCommand"/> — see the command summary for the
/// onboarding-atom contract. The three writes (Project, ProjectBranch,
/// ProjectRuntime) are tracked together and committed in a single
/// <c>SaveChangesAsync</c> call, which gives us atomicity without an explicit
/// transaction (EF's default behaviour wraps a single SaveChanges in a
/// transaction).
///
/// <para>Failure error strings are prefixed so the controller can map them to
/// the right HTTP status:</para>
/// <list type="bullet">
///   <item><c>forbidden:</c> caller is not a member of the workspace → 403;</item>
///   <item><c>pool_empty</c> — preview-subdomain pool is exhausted → 409 (the
///         controller maps this verbatim);</item>
///   <item>everything else is a validation problem → 400.</item>
/// </list>
/// </summary>
public sealed class CreateProjectHandler : ICommandHandler<CreateProjectCommand, Result<CreateProjectOutcome>>
{
    /// <summary>
    /// Sentinel prefix for "the caller is not allowed to do this" — the
    /// <c>ProjectsController</c> matches on this to return 403 instead of 400.
    /// </summary>
    public const string ForbiddenPrefix = "forbidden:";

    /// <summary>
    /// Sentinel error string returned (verbatim, no prefix) when the preview-
    /// subdomain pool is exhausted at branch-claim time. The controller maps
    /// this to HTTP 409 Conflict with a body of <c>{ "error": "pool_empty" }</c>
    /// so the frontend can surface the spec's "ask your admin to batch-create
    /// more" message without parsing free-form strings.
    /// </summary>
    public const string PoolEmptyError = "pool_empty";

    /// <summary>
    /// Stable machine-readable error code surfaced inside the
    /// <see cref="RepositoryAlreadyLinkedConflict"/> body when the requested repo already
    /// belongs to a live project in the same workspace. Mirrors the
    /// <c>BranchAlreadyLinked</c> code used by <c>AttachGitBranchHandler</c> so the
    /// frontend can switch on a single field across both shapes.
    /// </summary>
    public const string RepositoryAlreadyLinkedCode = "RepositoryAlreadyLinked";

    /// <summary>
    /// Sentinel mirroring <see cref="CopyBranch.CopyBranchHandler.CatalogSpecNotFoundError"/>
    /// and <see cref="ForkBranchFromGit.ForkBranchFromGitHandler.CatalogSpecNotFoundError"/>.
    /// Returned when the caller's <c>CatalogSpecId</c> doesn't resolve, or
    /// resolves to a row in a different workspace than the target. The
    /// controller maps it to 404 — existence-safe so a cross-workspace probe
    /// can't differentiate.
    /// </summary>
    public const string CatalogSpecNotFoundError = "catalog_spec_not_found";

    /// <summary>
    /// Sentinel returned when the caller asks to create a brand-new repo but doesn't
    /// supply a valid name. Maps to 400 in the controller.
    /// </summary>
    public const string NewRepoNameInvalidError = "new_repo_name_invalid";

    /// <summary>
    /// Sentinel returned when GitHub refuses to create the brand-new repo
    /// (name collision, permission issue, etc.). The actual GitHub-side message is
    /// preserved in the error string after the prefix so the frontend can surface it.
    /// </summary>
    public const string GithubRepoCreateFailedPrefix = "github_repo_create_failed:";

    /// <summary>
    /// Sentinel returned when <see cref="CreateProjectCommand.TemplateId"/> is set
    /// but does not resolve to an active, non-archived
    /// <see cref="Source.Features.ProjectTemplates.Models.ProjectTemplate"/>. The
    /// controller maps it to 404 — existence-safe and matches the
    /// <c>catalog_spec_not_found</c> shape next door.
    /// </summary>
    public const string TemplateNotFoundError = "template_not_found";

    /// <summary>
    /// Stable machine-readable error code surfaced when the create-repo path needs a UAT
    /// (User installation) but doesn't have one — or has one that can't be refreshed. The
    /// frontend pivots on this exact string to render the "Re-authorize GitHub" banner
    /// instead of the generic error UI.
    /// </summary>
    public const string GithubUserAuthRequiredError = "github_user_auth_required";

    private readonly ApplicationDbContext _db;
    private readonly IFlyOptionsAccessor _flyOptions;
    private readonly IMediator _mediator;
    private readonly IBackgroundJobClient _backgroundJobs;
    private readonly IGithubApiClient _githubApiClient;
    private readonly IGithubUserTokenService _userTokenService;
    private readonly ILogger<CreateProjectHandler> _logger;

    public CreateProjectHandler(
        ApplicationDbContext db,
        IFlyOptionsAccessor flyOptions,
        IMediator mediator,
        IBackgroundJobClient backgroundJobs,
        IGithubApiClient githubApiClient,
        IGithubUserTokenService userTokenService,
        ILogger<CreateProjectHandler> logger)
    {
        _db = db;
        _flyOptions = flyOptions;
        _mediator = mediator;
        _backgroundJobs = backgroundJobs;
        _githubApiClient = githubApiClient;
        _userTokenService = userTokenService;
        _logger = logger;
    }

    public async Task<Result<CreateProjectOutcome>> Handle(CreateProjectCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CallerUserId))
            return Result.Failure<CreateProjectOutcome>($"{ForbiddenPrefix} caller is not authenticated");

        // Validate inputs differently depending on the create mode. In "brand new repo" mode
        // only NewRepoName is mandatory — the repo coordinates come from the GitHub response.
        if (request.CreateNewRepo)
        {
            if (string.IsNullOrWhiteSpace(request.NewRepoName))
                return Result.Failure<CreateProjectOutcome>(NewRepoNameInvalidError);
            // GitHub allows alphanumerics, hyphens, underscores, and periods. We pre-validate
            // loosely here to give a fast 400 — GitHub's own validation is the final word.
            var trimmedNewName = request.NewRepoName.Trim();
            if (trimmedNewName.Length < 1 || trimmedNewName.Length > 100)
                return Result.Failure<CreateProjectOutcome>(NewRepoNameInvalidError);
            for (int i = 0; i < trimmedNewName.Length; i++)
            {
                var c = trimmedNewName[i];
                var ok = char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.';
                if (!ok) return Result.Failure<CreateProjectOutcome>(NewRepoNameInvalidError);
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.RepoOwner))
                return Result.Failure<CreateProjectOutcome>("Repo owner is required");
            if (string.IsNullOrWhiteSpace(request.RepoName))
                return Result.Failure<CreateProjectOutcome>("Repo name is required");
            if (string.IsNullOrWhiteSpace(request.BranchName))
                return Result.Failure<CreateProjectOutcome>("Branch name is required");
        }

        // -------- 1. Membership check (403 on failure) --------
        var isMember = await _db.WorkspaceMemberships
            .AsNoTracking()
            .AnyAsync(
                m => m.WorkspaceId == request.WorkspaceId && m.UserId == request.CallerUserId,
                cancellationToken);

        if (!isMember)
        {
            return Result.Failure<CreateProjectOutcome>(
                $"{ForbiddenPrefix} caller is not a member of the target workspace");
        }

        // -------- 2. Installation check (400 on failure) --------
        // Installation must exist AND belong to the same workspace — otherwise
        // a member of workspace A could attach an installation owned by
        // workspace B to their project, leaking access to its repos.
        // We pull the full row when CreateNewRepo is true because we need the
        // InstallationId + AccountLogin + AccountType to drive the GitHub API call.
        //
        // Tracked vs AsNoTracking: for the create-new-repo or template branches we may
        // refresh the UAT via IGithubUserTokenService, which writes back to the row.
        // The connect-existing-repo branch doesn't touch the UAT so AsNoTracking is fine
        // there — but uniformly tracking is simpler and the row is tiny.
        var needsTracking = request.CreateNewRepo || request.TemplateId.HasValue;
        var installationQuery = _db.GithubInstallations.AsQueryable();
        if (!needsTracking) installationQuery = installationQuery.AsNoTracking();
        var installation = await installationQuery
            .FirstOrDefaultAsync(
                i => i.Id == request.GithubInstallationId && i.WorkspaceId == request.WorkspaceId,
                cancellationToken);

        if (installation is null)
        {
            return Result.Failure<CreateProjectOutcome>(
                "GitHub installation does not exist or does not belong to this workspace");
        }

        // -------- 2.5. Create brand-new repo on GitHub (when CreateNewRepo == true) --------
        // We do this BEFORE the duplicate-repo check so the coordinates we check against
        // are the freshly-created repo's. The brand-new repo is guaranteed not to collide
        // (GitHub would have returned 422 inside the API call) so the duplicate check is
        // effectively a no-op here, but we keep the same downstream code path.
        string effectiveRepoOwner;
        string effectiveRepoName;
        string effectiveBranchName;

        // Starters-specific carry-over from the template branch. When the
        // template branch fires, these hold the resolved template's id
        // (persisted onto the new Project) and its inline RuntimeSpec JSON
        // (snapshotted onto the new ProjectRuntime). When TemplateId is null
        // these stay null and every downstream step runs untouched.
        Guid? resolvedTemplateId = null;
        string? templateRuntimeSpecJson = null;

        // Starters branch — runs FIRST, before the existing CreateNewRepo /
        // connect-existing-repo branches. Entered ONLY when the caller passed
        // a TemplateId. When null, the existing if/else chain below runs
        // byte-identically to today.
        if (request.TemplateId is { } templateId)
        {
            // GLOBAL scope — starters are super-admin curated and shared
            // across every workspace, so there is intentionally NO
            // WorkspaceId filter here. IsActive + !IsDeleted gates archived
            // and tombstoned rows. ISoftDelete's global query filter already
            // hides IsDeleted == true rows, but we mirror the predicate
            // explicitly so the intent stays visible at the call site.
            var template = await _db.ProjectTemplates
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    t => t.Id == templateId && t.IsActive && !t.IsDeleted,
                    cancellationToken);

            if (template is null)
            {
                return Result.Failure<CreateProjectOutcome>(TemplateNotFoundError);
            }

            // The new repo's name falls back to the template's Slug when the
            // caller didn't supply NewRepoName — matches the spec ("Maya names
            // her project 'focus-flow'" or accepts the curated default).
            // Description is passed through verbatim — null is fine.
            var newRepoName = !string.IsNullOrWhiteSpace(request.NewRepoName)
                ? request.NewRepoName!.Trim()
                : template.Slug;

            // Token selection: Org installs → IAT (installation token). User installs →
            // UAT (user access token) via IGithubUserTokenService. GitHub's
            // /repos/{tmpl}/generate endpoint silently rejects IATs when the target
            // owner is a User account, so we must route those through the UAT path.
            var isUserInstall = !string.Equals(installation.AccountType, "Organization", StringComparison.OrdinalIgnoreCase);

            GithubRepoDto createdFromTemplate;
            try
            {
                if (isUserInstall)
                {
                    string uat;
                    try
                    {
                        uat = await _userTokenService.GetValidUserAccessTokenAsync(installation, cancellationToken);
                    }
                    catch (GithubUserAuthRequiredException ex)
                    {
                        _logger.LogInformation(ex,
                            "Starter-from-template create rejected: UAT required for User installation {InstallationId} ({Login}); reason {Reason}",
                            installation.InstallationId, installation.AccountLogin, ex.ReasonCode);
                        return Result.Failure<CreateProjectOutcome>(
                            $"{GithubUserAuthRequiredError}: GitHub needs to re-authorize. Reason: {ex.ReasonCode}");
                    }

                    createdFromTemplate = await _githubApiClient.CreateRepoFromTemplateWithTokenAsync(
                        accessToken: uat,
                        templateOwner: template.SourceRepoOwner,
                        templateRepo: template.SourceRepoName,
                        newOwner: installation.AccountLogin,
                        newRepoName: newRepoName,
                        description: request.NewRepoDescription?.Trim(),
                        isPrivate: true,
                        ct: cancellationToken);

                    // Selected-repos installs don't auto-include the newly created repo.
                    // Soft-fail: the repo still exists; worst case the user adds it manually.
                    await TryAddRepoToUserInstallationAsync(uat, installation.InstallationId, createdFromTemplate.Id, cancellationToken);
                }
                else
                {
                    createdFromTemplate = await _githubApiClient.CreateRepoFromTemplateAsync(
                        installationId: installation.InstallationId,
                        templateOwner: template.SourceRepoOwner,
                        templateRepo: template.SourceRepoName,
                        newOwner: installation.AccountLogin,
                        newRepoName: newRepoName,
                        description: request.NewRepoDescription?.Trim(),
                        isPrivate: true,
                        ct: cancellationToken);
                }
            }
            catch (GitHubRepoCreateFailedException ex)
            {
                _logger.LogWarning(ex, "GitHub repo-from-template creation failed during project create");
                return Result.Failure<CreateProjectOutcome>(
                    $"{GithubRepoCreateFailedPrefix} {ex.Message}");
            }

            effectiveRepoOwner = createdFromTemplate.Owner.Login;
            effectiveRepoName = createdFromTemplate.Name;
            // Mirror the CreateNewRepo branch: GitHub picks the default branch
            // from the template (typically "main"); fall back to "main" on null.
            effectiveBranchName = string.IsNullOrWhiteSpace(createdFromTemplate.DefaultBranch)
                ? "main"
                : createdFromTemplate.DefaultBranch;

            // Stash for the runtime-spec snapshot + the Project.TemplateId
            // FK assignment further down. RuntimeSpec is opaque JSON on the
            // template entity — we snapshot it verbatim onto ProjectRuntime.Spec,
            // same destination CatalogSpecId currently writes to. Null is fine
            // and means "Empty" starter → runtime boots with default/empty spec
            // (today's no-starter behaviour).
            resolvedTemplateId = template.Id;
            templateRuntimeSpecJson = template.RuntimeSpec;

            _logger.LogInformation(
                "Created repo {Owner}/{Name} from template {TemplateSlug} ({TemplateOwner}/{TemplateRepo}) for project bootstrap",
                effectiveRepoOwner, effectiveRepoName, template.Slug, template.SourceRepoOwner, template.SourceRepoName);
        }
        else if (request.CreateNewRepo)
        {
            // Token selection — see the template branch above for the rationale. Org
            // installs use the App's installation token (IAT). User installs MUST use
            // a User Access Token (UAT) because POST /user/repos rejects IATs for the
            // user-account namespace. The "Brand-new repo requires Organization
            // installation" pre-flight check is gone — UATs unblock the User path.
            var isUserInstall = !string.Equals(installation.AccountType, "Organization", StringComparison.OrdinalIgnoreCase);

            GithubRepoDto createdRepo;
            try
            {
                if (isUserInstall)
                {
                    string uat;
                    try
                    {
                        uat = await _userTokenService.GetValidUserAccessTokenAsync(installation, cancellationToken);
                    }
                    catch (GithubUserAuthRequiredException ex)
                    {
                        _logger.LogInformation(ex,
                            "Brand-new-repo create rejected: UAT required for User installation {InstallationId} ({Login}); reason {Reason}",
                            installation.InstallationId, installation.AccountLogin, ex.ReasonCode);
                        return Result.Failure<CreateProjectOutcome>(
                            $"{GithubUserAuthRequiredError}: GitHub needs to re-authorize. Reason: {ex.ReasonCode}");
                    }

                    // POST /user/repos accepts the same UAT, but we re-use the
                    // generate-from-template-with-token path... actually no — empty repo
                    // creation under a user goes through a different endpoint. Use the
                    // dedicated UAT-authenticated /user/repos call below.
                    createdRepo = await CreateUserRepoWithUatAsync(
                        uat,
                        request.NewRepoName!.Trim(),
                        request.NewRepoDescription?.Trim(),
                        isPrivate: true,
                        cancellationToken);

                    await TryAddRepoToUserInstallationAsync(uat, installation.InstallationId, createdRepo.Id, cancellationToken);
                }
                else
                {
                    createdRepo = await _githubApiClient.CreateInstallationRepositoryAsync(
                        installationId: installation.InstallationId,
                        ownerLogin: installation.AccountLogin,
                        accountType: installation.AccountType,
                        name: request.NewRepoName!.Trim(),
                        description: request.NewRepoDescription?.Trim(),
                        isPrivate: true,
                        defaultBranch: "main",
                        ct: cancellationToken);
                }
            }
            catch (GitHubRepoCreateFailedException ex)
            {
                _logger.LogWarning(ex, "GitHub repo creation failed during project create");
                return Result.Failure<CreateProjectOutcome>(
                    $"{GithubRepoCreateFailedPrefix} {ex.Message}");
            }

            effectiveRepoOwner = createdRepo.Owner.Login;
            effectiveRepoName = createdRepo.Name;
            // Use the repo's actual default branch (typically "main") — GitHub picks this
            // from the account's repo defaults and we don't get to override it at create
            // time. Fall back to "main" if for some reason it came back null.
            effectiveBranchName = string.IsNullOrWhiteSpace(createdRepo.DefaultBranch)
                ? "main"
                : createdRepo.DefaultBranch;

            _logger.LogInformation(
                "Created fresh GitHub repo {Owner}/{Name} (default branch: {Branch}) for project bootstrap",
                effectiveRepoOwner, effectiveRepoName, effectiveBranchName);
        }
        else
        {
            effectiveRepoOwner = request.RepoOwner;
            effectiveRepoName = request.RepoName;
            effectiveBranchName = request.BranchName;
        }

        // -------- 2a. Duplicate-repo check (409 RepositoryAlreadyLinked) --------
        // A workspace can host at most ONE project per (installation, owner, name) tuple.
        // We check BEFORE the runtime-precondition gates so the user gets the most
        // actionable error first — pointing them at the existing project is better UX
        // than "no active runtime image, ask an admin" when the click is going to be
        // rejected as a duplicate anyway. Soft-deleted projects are already excluded by
        // the global query filter on Project (ISoftDelete), so this won't false-positive
        // on a tombstoned row.
        //
        // Trim mirrors the trim we'll apply when persisting (lines below) so the dedup
        // check sees the same canonical form. Case-insensitive compare matches GitHub's
        // own repo lookup semantics — "Foo/Bar" and "foo/bar" point at the same repo.
        var trimmedOwner = effectiveRepoOwner.Trim();
        var trimmedName = effectiveRepoName.Trim();
        var existingProject = await _db.Projects
            .AsNoTracking()
            .Where(p => p.WorkspaceId == request.WorkspaceId
                        && p.GithubInstallationId == request.GithubInstallationId
                        && EF.Functions.ILike(p.GithubRepoOwner, trimmedOwner)
                        && EF.Functions.ILike(p.GithubRepoName, trimmedName))
            .Select(p => new { p.Id, p.Name })
            .FirstOrDefaultAsync(cancellationToken);

        if (existingProject is not null)
        {
            return Result.Success(new CreateProjectOutcome(
                Project: null,
                Conflict: new RepositoryAlreadyLinkedConflict(
                    Code: RepositoryAlreadyLinkedCode,
                    Message: $"This repository already belongs to project '{existingProject.Name}'. Open it instead.",
                    ExistingProjectId: existingProject.Id,
                    ExistingProjectName: existingProject.Name)));
        }

        // -------- 2b. Runtime provisioning preconditions (400 on failure) --------
        // Fail-fast at the create path so a misconfigured platform never lands a
        // project in a permanently-Pending state. The RuntimeProvisionerJob has
        // matching guards as a belt-and-braces, but blocking here gives the user
        // an immediate, actionable error instead of an indefinite "Provisioning…".

        var hasActiveImage = await _db.RuntimeImages
            .AnyAsync(i => i.Status == RuntimeImageStatus.Active, cancellationToken);
        if (!hasActiveImage)
        {
            return Result.Failure<CreateProjectOutcome>(
                "No active runtime image is registered. Ask an admin to activate one in Super Admin → Runtime Images.");
        }

        // Cheap presence check on Fly settings — NOT a live API ping. The
        // /api/admin/fly/test-connection endpoint is the right tool for liveness;
        // here we just want "can the provisioner even attempt the call?"
        var fly = _flyOptions.Current;
        if (string.IsNullOrWhiteSpace(fly.ApiToken) ||
            string.IsNullOrWhiteSpace(fly.OrgSlug) ||
            string.IsNullOrWhiteSpace(fly.AppName))
        {
            return Result.Failure<CreateProjectOutcome>(
                "Fly settings are incomplete. Configure them in Super Admin → System Settings.");
        }

        // -------- 2c. Resolve catalog spec (workspace-spec-catalog Scene 3) --------
        // The optional CatalogSpecId arms the "starting spec" picker on the
        // new-project dialog. Null means "Blank — no services" (the default
        // for new projects, since there's no source branch to carry from).
        // A provided id must resolve to a row that belongs to the SAME
        // workspace as the new project; cross-workspace ids collapse to the
        // existence-safe CatalogSpecNotFoundError sentinel.
        string? newSpec = null;
        int newSpecVersion = 1;
        if (request.CatalogSpecId.HasValue)
        {
            var catalogSpec = await _db.WorkspaceSpecs
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == request.CatalogSpecId.Value, cancellationToken);

            if (catalogSpec is null || catalogSpec.WorkspaceId != request.WorkspaceId)
            {
                return Result.Failure<CreateProjectOutcome>(CatalogSpecNotFoundError);
            }

            // Snapshot by string assignment — future edits to the catalog
            // entry never retro-touch this row. See WorkspaceSpec.cs Scene 4.
            newSpec = catalogSpec.Content;
            newSpecVersion = 2; // catalog content is canonically V2 RuntimeSpec
        }

        // Starters: when the template branch resolved a curated RuntimeSpec,
        // snapshot it into the SAME runtime-spec destination CatalogSpecId
        // writes to. Inline JSON on the template, snapshot-by-string-assignment
        // here — no FK and no shared mutation: future edits to the template
        // never retro-touch this row. Null (Empty starter, or template with no
        // curated recipe) leaves the runtime spec as whatever the CatalogSpecId
        // path set (typically null = "Blank — no services"), matching today's
        // no-starter behaviour. The starter feature spec calls this out
        // explicitly under "Reuse, don't rebuild".
        if (templateRuntimeSpecJson is not null)
        {
            newSpec = templateRuntimeSpecJson;
            newSpecVersion = 2; // template RuntimeSpec is canonically V2
        }

        // -------- 3. Build the three rows --------
        var project = new Project
        {
            Id = Guid.NewGuid(),
            WorkspaceId = request.WorkspaceId,
            OwnerUserId = request.CallerUserId,
            // Use the repo name as the initial display name for the smoke test.
            // The frontend can rename later via a dedicated endpoint.
            Name = effectiveRepoName.Trim(),
            GithubRepoOwner = effectiveRepoOwner.Trim(),
            GithubRepoName = effectiveRepoName.Trim(),
            GithubInstallationId = request.GithubInstallationId,
            TemplateId = resolvedTemplateId,
            // Project-level runtime spec — resolved in step 2c above (catalog
            // / template / blank). Per `project-level-runtime-spec`, the spec
            // lives on the project and every runtime under it inherits via
            // bootstrap. Null = "no spec yet" (the historical blank default).
            Spec = newSpec,
            SpecVersion = newSpecVersion,
        };

        // Apply optional preview-port override before save. Validation lives on
        // the entity — invalid values short-circuit with a clean 400 here
        // instead of letting a bad row land in the DB.
        if (request.PreviewPort.HasValue)
        {
            var setPortResult = project.SetPreviewPort(request.PreviewPort.Value);
            if (!setPortResult.IsSuccess)
            {
                return Result.Failure<CreateProjectOutcome>(setPortResult.Error!);
            }
        }

        var branch = new ProjectBranch
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Name = effectiveBranchName.Trim(),
            IsDefault = true,
        };

        // Region default mirrors the existing admin onboarding endpoint
        // (RuntimeProvisionController) so the smoke-test runtime lands in the
        // same Fly region as everything else.
        var runtime = new ProjectRuntime
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            BranchId = branch.Id,
            // TenantId carries the workspace boundary all the way to the
            // daemon's rt_tenant JWT claim — critical for the tenancy flow per
            // the e2e-smoketest spec.
            TenantId = request.WorkspaceId,
            State = RuntimeState.Pending,
            StateChangedAt = DateTime.UtcNow,
            Region = "arn",
            // Runtime SERVICES spec now lives on Project (resolved above and
            // assigned to project.Spec / project.SpecVersion). The runtime row
            // no longer carries a Spec field — bootstrap reads from the
            // project on every cold-boot. See `project-level-runtime-spec`.
            // Runtime MACHINE spec (CPU/RAM/disk) — snapshot the project's
            // current default so the Fly machine inherits the per-project Fly
            // sizing (CPU class, cores, RAM, volume size). Defaults match the
            // historical MachineGuest() tuple — see Project.DefaultRuntime* —
            // so nothing changes for projects that haven't customised their
            // spec.
            CpuKind = project.RuntimeCpuKind,
            Cpus = project.RuntimeCpus,
            MemoryMb = project.RuntimeMemoryMb,
            VolumeSizeGb = project.RuntimeVolumeSizeGb,
        };

        _db.Projects.Add(project);
        _db.ProjectBranches.Add(branch);
        _db.ProjectRuntimes.Add(runtime);

        // Raise ProjectCreated — downstream handlers (audit, telemetry) react
        // via the DomainEventInterceptor after SaveChanges commits.
        project.MarkCreated();

        // -------- 4. Atomic subdomain claim + DB write --------
        // cloudflare-tunnel-preview Phase 3: every branch gets a preview
        // subdomain claimed from the pool at creation time. Wrap the claim
        // and the SaveChanges in an explicit transaction so both writes
        // commit together (or both roll back). AssignSubdomainToBranchHandler
        // detects the ambient tx and defers its SaveChanges to ours.
        //
        // Execution-strategy note: Npgsql's retrying execution strategy
        // (enabled in DatabaseExtensions.cs with EnableRetryOnFailure) forbids
        // user-initiated transactions outside its ExecuteAsync wrapper — it
        // can't retry across a BeginTransaction/Commit boundary it doesn't
        // own. So we route the whole tx body through CreateExecutionStrategy()
        // / ExecuteAsync so the strategy controls the retry boundary.
        //
        // We surface two distinct failure shapes back to the caller:
        //   * pool_empty (controller → 409)
        //   * DbUpdateException uniqueness clash (controller → 400)
        // ExecuteAsync doesn't fit a 3-way result naturally, so we pack the
        // outcome into a discriminator string + the strategy returns the
        // outcome. The caller (this method) then maps to Result<ProjectDto>.
        const string outcomePoolEmpty = "pool_empty";
        const string outcomeDbUpdate = "db_update";
        const string outcomeOk = "ok";

        var strategy = _db.Database.CreateExecutionStrategy();
        var (outcome, errorDetail) = await strategy.ExecuteAsync<(string Outcome, string? Error)>(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

            var assignResult = await _mediator.Send(
                new AssignSubdomainToBranchCommand(branch.Id),
                cancellationToken);

            if (!assignResult.IsSuccess)
            {
                // pool_empty (or any future claim-side failure) — abort the
                // whole creation. No partial branch is left behind because we
                // haven't SaveChanged yet.
                await tx.RollbackAsync(cancellationToken);
                return (outcomePoolEmpty, assignResult.Error ?? PoolEmptyError);
            }

            try
            {
                // Single SaveChanges flushes Project + Branch + Runtime AND the
                // mutated SubdomainAssignment row tracked by AssignSubdomainTo-
                // BranchHandler. Single transaction commit = atomic.
                await _db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                return (outcomeOk, null);
            }
            catch (DbUpdateException)
            {
                await tx.RollbackAsync(cancellationToken);
                return (outcomeDbUpdate, null);
            }
        });

        if (outcome == outcomePoolEmpty)
        {
            return Result.Failure<CreateProjectOutcome>(errorDetail ?? PoolEmptyError);
        }
        if (outcome == outcomeDbUpdate)
        {
            return Result.Failure<CreateProjectOutcome>(
                "Failed to create project — the workspace, repo or branch may already exist");
        }

        // Kick the provisioner immediately for the newly-Pending runtime — the
        // minutely sweep stays in place as a safety net for the rare race
        // where the row commits but this enqueue doesn't. Outside the strategy
        // wrapper on purpose: the strategy may retry on transient DB faults,
        // and we only want to enqueue once a real commit landed.
        _backgroundJobs.Enqueue<RuntimeProvisionerJob>(
            j => j.ProvisionOne(runtime.Id, JobCancellationToken.Null));

        return Result.Success(new CreateProjectOutcome(
            Project: new ProjectDto(
                Id: project.Id,
                Name: project.Name,
                WorkspaceId: project.WorkspaceId,
                DefaultBranchId: branch.Id,
                DefaultBranchName: branch.Name,
                RuntimeId: runtime.Id,
                RuntimeState: runtime.State,
                GithubRepoOwner: project.GithubRepoOwner,
                GithubRepoName: project.GithubRepoName,
                GithubInstallationId: project.GithubInstallationId,
                PreviewPort: project.PreviewPort,
                RuntimeCpuKind: project.RuntimeCpuKind,
                RuntimeCpus: project.RuntimeCpus,
                RuntimeMemoryMb: project.RuntimeMemoryMb,
                RuntimeVolumeSizeGb: project.RuntimeVolumeSizeGb,
                ModelId: project.ModelId,
                ModelSlug: null),
            Conflict: null));
    }

    /// <summary>
    /// Thin wrapper around <see cref="IGithubApiClient.CreateUserRepoWithTokenAsync"/> kept here so the
    /// brand-new-repo branch reads like its sibling template branch (which calls the API client directly
    /// via <c>CreateRepoFromTemplateWithTokenAsync</c>). No behaviour beyond delegation — extracted for
    /// readability against the rest of the handler.
    /// </summary>
    private Task<GithubRepoDto> CreateUserRepoWithUatAsync(
        string uat,
        string name,
        string? description,
        bool isPrivate,
        CancellationToken ct)
    {
        return _githubApiClient.CreateUserRepoWithTokenAsync(uat, name, description, isPrivate, ct);
    }

    /// <summary>
    /// Add a freshly-created repo to a User installation's selected-repo list. Soft-fail: if GitHub
    /// rejects the call (selected-repos scope not in use, repo already added, transient network blip),
    /// we log a warning but DO NOT fail the create. The repo still exists; worst case the user adds it
    /// manually via GitHub's UI.
    /// </summary>
    private async Task TryAddRepoToUserInstallationAsync(string uat, long installationId, long repositoryId, CancellationToken ct)
    {
        try
        {
            await _githubApiClient.AddRepoToUserInstallationAsync(uat, installationId, repositoryId, ct);
            _logger.LogInformation(
                "Added repo {RepositoryId} to user installation {InstallationId}",
                repositoryId, installationId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to add repo {RepositoryId} to user installation {InstallationId}; the repo exists, but the App may not get webhooks for it until the user adds it manually",
                repositoryId, installationId);
        }
    }
}

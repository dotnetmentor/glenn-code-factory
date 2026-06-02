using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Source.Features.GitHub.Models;
using Source.Features.Projects.Commands.ArchiveBranch;
using Source.Features.Projects.Commands.AssignBranchSubdomain;
using Source.Features.Projects.Commands.AttachGitBranch;
using Source.Features.Projects.Commands.CopyBranch;
using Source.Features.Projects.Commands.CreateProject;
using Source.Features.Projects.Commands.DeleteProject;
using Source.Features.Projects.Commands.ForkBranchFromGit;
using Source.Features.Projects.Commands.UnarchiveBranch;
using Source.Features.Projects.Commands.RenameProject;
using Source.Features.Projects.Commands.UpdatePreviewPort;
using Source.Features.Projects.Commands.UpdateProjectByok;
using Source.Features.Projects.Commands.UpdateProjectCursorModel;
using Source.Features.Projects.Commands.UpdateRuntimeSpec;
using Source.Features.Projects.Models;
using Source.Features.Projects.Queries.GetProject;
using Source.Features.Projects.Queries.ListProjectBranches;
using Source.Features.Projects.Queries.ListProjectGithubBranches;
using Source.Features.RuntimeLifecycle.Models;
using Source.Shared.Controllers;

namespace Source.Features.Projects.Controllers;

/// <summary>
/// User-facing endpoints for the <see cref="Models.Project"/> entity. The only
/// action on this card is <c>POST /api/projects</c> — the onboarding atom that
/// creates a project, its default branch and a Pending runtime in one
/// transaction. The recurring <c>RuntimeProvisionerJob</c> takes the runtime
/// from there.
/// </summary>
[ApiController]
[Route("api/projects")]
[Authorize]
[Tags("Projects")]
public class ProjectsController : BaseApiController
{
    public ProjectsController(IMediator mediator, ILogger<ProjectsController> logger)
        : base(mediator, logger) { }

    /// <summary>
    /// Create a new project inside a workspace. The caller must be a member of
    /// the workspace; the GitHub installation must belong to the same
    /// workspace. Returns the newly-created <see cref="ProjectDto"/> with the
    /// runtime in <c>Pending</c> state.
    ///
    /// <para><b>Duplicate-repo rejection.</b> If the requested (workspace,
    /// installation, repo) already maps to a live project the response is a
    /// 409 with a <see cref="RepositoryAlreadyLinkedConflict"/> body carrying
    /// the existing project's id + name so the frontend can render an
    /// actionable "Open it instead" link. This mirrors the
    /// <c>BranchAlreadyLinked</c> shape <c>AttachBranch</c> returns.</para>
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(ProjectDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(RepositoryAlreadyLinkedConflict), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProjectDto>> Create([FromBody] CreateProjectRequest request)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var result = await Mediator.Send(new CreateProjectCommand(
            CallerUserId: userId,
            WorkspaceId: request.WorkspaceId,
            GithubInstallationId: request.GithubInstallationId,
            RepoOwner: request.RepoOwner ?? string.Empty,
            RepoName: request.RepoName ?? string.Empty,
            BranchName: request.BranchName ?? "main",
            PreviewPort: request.PreviewPort,
            CatalogSpecId: request.CatalogSpecId,
            CreateNewRepo: request.CreateNewRepo,
            NewRepoName: request.NewRepoName,
            NewRepoDescription: request.NewRepoDescription,
            TemplateId: request.TemplateId));

        if (!result.IsSuccess)
        {
            // Membership failures are encoded with the ForbiddenPrefix sentinel
            // so we can map them to 403 here without leaking the underlying
            // detail (which would let an attacker probe workspace ids).
            if (result.Error?.StartsWith(CreateProjectHandler.ForbiddenPrefix, StringComparison.Ordinal) == true)
            {
                Logger.LogWarning("Project creation forbidden: {Error}", result.Error);
                return Forbid();
            }

            // cloudflare-tunnel-preview Phase 3: pool_empty is resource
            // exhaustion, not malformed input — 409 Conflict matches the
            // spec's "ask your admin to batch-create more" recovery message
            // and lets the frontend key off a stable error code.
            if (string.Equals(result.Error, CreateProjectHandler.PoolEmptyError, StringComparison.Ordinal))
            {
                Logger.LogWarning("Project creation failed: preview subdomain pool exhausted");
                return Conflict(new { error = result.Error });
            }

            // workspace-spec-catalog: the caller passed a CatalogSpecId that
            // doesn't resolve or belongs to a different workspace. Existence-
            // safe 404 — a member of workspace A must not be able to probe
            // whether catalog id X exists in workspace B.
            if (string.Equals(result.Error, CreateProjectHandler.CatalogSpecNotFoundError, StringComparison.Ordinal))
            {
                Logger.LogInformation("Project creation rejected: catalog spec not found");
                return NotFound(new { error = result.Error });
            }

            // Starters: the caller passed a TemplateId that doesn't resolve,
            // is archived (IsActive == false), or is soft-deleted. 404 mirrors
            // the catalog_spec_not_found shape next door — same picker UX in
            // the frontend.
            if (string.Equals(result.Error, CreateProjectHandler.TemplateNotFoundError, StringComparison.Ordinal))
            {
                Logger.LogInformation("Project creation rejected: template not found");
                return NotFound(new { error = result.Error });
            }

            // Brand-new-repo mode: invalid name → 400 with stable code.
            if (string.Equals(result.Error, CreateProjectHandler.NewRepoNameInvalidError, StringComparison.Ordinal))
            {
                Logger.LogInformation("Project creation rejected: invalid new repo name");
                return BadRequest(new { error = result.Error });
            }

            // Brand-new-repo mode: GitHub refused to create the repo. The detail string
            // includes GitHub's own error message (e.g. "name already exists on this account").
            if (result.Error?.StartsWith(CreateProjectHandler.GithubRepoCreateFailedPrefix, StringComparison.Ordinal) == true)
            {
                Logger.LogWarning("Project creation failed: GitHub refused repo create: {Error}", result.Error);
                return BadRequest(new { error = result.Error });
            }

            Logger.LogWarning("Project creation failed: {Error}", result.Error);
            return BadRequest(new { error = result.Error });
        }

        // Discriminated outcome: either the new project (200) or a duplicate-
        // repo rejection (409). The handler models duplicate as a structured
        // SUCCESS rather than a sentinel-error string because the response body
        // is meaningful payload (existingProjectId + existingProjectName), not
        // just an error code — surfacing it through the failure channel would
        // force us to JSON-encode the payload inside the error string, which
        // is exactly the kind of stringly-typed wire we don't want.
        var outcome = result.Value;
        if (outcome.Conflict is { } conflict)
        {
            Logger.LogInformation(
                "Project creation rejected as duplicate: existing project {ExistingProjectId} ({ExistingProjectName})",
                conflict.ExistingProjectId, conflict.ExistingProjectName);
            return Conflict(conflict);
        }

        // Project must be non-null when Conflict is null — outcome is a
        // discriminated union with exactly one populated arm on success.
        return Ok(outcome.Project!);
    }

    /// <summary>
    /// Load a single project for the project workspace shell page. The caller
    /// must be a member of the project's workspace (workspace owners are
    /// members by construction). Non-members and missing/soft-deleted ids both
    /// collapse to a 404 so an attacker cannot probe project existence — same
    /// "don't leak existence" gate as the BYOK endpoint.
    ///
    /// <para>Returns the same <see cref="ProjectDto"/> shape <c>POST /api/projects</c>
    /// emits, so the frontend can hydrate the same view model whether it just
    /// created the project or is loading it cold from a deep link.</para>
    /// </summary>
    [HttpGet("{projectId:guid}")]
    [ProducesResponseType(typeof(ProjectDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProjectDto>> Get(Guid projectId)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var result = await Mediator.Send(new GetProjectQuery(
            ProjectId: projectId,
            CallerUserId: userId));

        if (!result.IsSuccess)
        {
            if (result.Error?.StartsWith(GetProjectHandler.NotFoundPrefix, StringComparison.Ordinal) == true)
            {
                Logger.LogInformation(
                    "GetProject: 404 for project {ProjectId} caller {UserId}: {Error}",
                    projectId, userId, result.Error);
                return NotFound();
            }

            Logger.LogWarning("GetProject failed: {Error}", result.Error);
            return BadRequest(new { error = result.Error });
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// List the branches of a project for the workspace shell's branch picker.
    /// Same workspace-membership gate as <see cref="Get"/> — non-members and
    /// missing project ids both return 404 so existence cannot be probed.
    /// Default branch is sorted to the top so the frontend's pre-selection
    /// (driven by <see cref="ProjectDto.DefaultBranchId"/>) matches the visible
    /// order.
    ///
    /// <para><b>Archived branches.</b> Hidden by default — sidebars and the
    /// branch picker only ever see active rows. Pass
    /// <c>?includeArchived=true</c> to surface every branch (active +
    /// archived); the Settings → Branches tab uses this to let the user pick
    /// rows to unarchive.</para>
    /// </summary>
    [HttpGet("{projectId:guid}/branches")]
    [ProducesResponseType(typeof(List<ProjectBranchDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<List<ProjectBranchDto>>> ListBranches(
        Guid projectId,
        [FromQuery] bool includeArchived = false)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var result = await Mediator.Send(new ListProjectBranchesQuery(
            ProjectId: projectId,
            CallerUserId: userId,
            IncludeArchived: includeArchived));

        if (!result.IsSuccess)
        {
            if (result.Error?.StartsWith(ListProjectBranchesHandler.NotFoundPrefix, StringComparison.Ordinal) == true)
            {
                Logger.LogInformation(
                    "ListProjectBranches: 404 for project {ProjectId} caller {UserId}: {Error}",
                    projectId, userId, result.Error);
                return NotFound();
            }

            Logger.LogWarning("ListProjectBranches failed: {Error}", result.Error);
            return BadRequest(new { error = result.Error });
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Partial-update a project's mutable settings. Workspace-Admin (or higher)
    /// gate — non-members and Members-without-admin both collapse to 404 so
    /// neither project existence nor the caller's exact privilege gap is
    /// leakable. The repo coordinates are independent and not touched here.
    ///
    /// <para>Body fields are all optional — omit a field (or send null) to
    /// leave it alone:</para>
    /// <list type="bullet">
    ///   <item><c>name</c> — trimmed, non-empty, ≤ <c>Project.MaxNameLength</c>
    ///         (100). Idempotent rename to the same trimmed value is a
    ///         no-op.</item>
    ///   <item><c>previewPort</c> — integer in 1..65535. Drives the
    ///         cloudflare-tunnel-preview tunnel ingress port. Idempotent set
    ///         to the current value is a no-op.</item>
    /// </list>
    /// <para>Returns 204 on success — frontend can re-fetch
    /// <c>GET /api/projects/{projectId}</c> if it needs the updated row.</para>
    /// </summary>
    [HttpPatch("{projectId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Rename(
        Guid projectId,
        [FromBody] RenameProjectRequest? request)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        if (request is null)
        {
            return BadRequest(new { error = "invalid_body" });
        }

        var result = await Mediator.Send(new RenameProjectCommand(
            ProjectId: projectId,
            CallerUserId: userId,
            Name: request.Name,
            PreviewPort: request.PreviewPort));

        if (!result.IsSuccess)
        {
            if (result.Error?.StartsWith(RenameProjectHandler.NotFoundPrefix, StringComparison.Ordinal) == true)
            {
                Logger.LogInformation(
                    "UpdateProject: 404 for project {ProjectId} caller {UserId}: {Error}",
                    projectId, userId, result.Error);
                return NotFound();
            }

            Logger.LogWarning(
                "UpdateProject validation failed for project {ProjectId}: {Error}",
                projectId, result.Error);
            return BadRequest(new { error = result.Error });
        }

        return NoContent();
    }

    /// <summary>
    /// Hot-swap the project's <c>PreviewPort</c> and propagate the new value
    /// to every assigned branch tunnel's Cloudflare ingress AND every open
    /// React tab via SignalR. Powers the realtime port-change UX — no daemon
    /// round-trip, no runtime reboot. Cloudflare's <c>PUT /configurations</c>
    /// is idempotent so re-running the same port is safe; the in-flight DB
    /// row is updated first so the new value is the source of truth even if
    /// a fan-out PUT fails mid-stream.
    ///
    /// <para><b>Auth.</b> Authenticated user only. The DB-level write is
    /// gated by the handler's project lookup — non-existent / soft-deleted
    /// ids return 400 "Project not found" rather than 404 so the response
    /// shape matches the rest of the realtime endpoints in this slice (the
    /// existing <c>PATCH /api/projects/{projectId}</c> uses the
    /// admin-role-or-better gate; this companion endpoint matches the same
    /// caller bar via the upstream JWT and lets the handler drive the rest).</para>
    ///
    /// <para><b>Response.</b> Returns the persisted port plus a count of how
    /// many tunnels were successfully repointed at the new port vs failed.
    /// Common case: <c>tunnelsFailed == 0</c>. A non-zero failed count is a
    /// signal for the UI to surface a warning but is NOT an error — the DB
    /// state is correct and the next port change picks up the stragglers.</para>
    /// </summary>
    [HttpPatch("{projectId:guid}/preview-port")]
    [ProducesResponseType(typeof(UpdateProjectPreviewPortResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<UpdateProjectPreviewPortResponse>> UpdatePreviewPort(
        Guid projectId,
        [FromBody] UpdatePreviewPortRequest? request,
        CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        if (request is null)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid body",
                Detail = "Request body required.",
            });
        }

        var result = await Mediator.Send(
            new UpdateProjectPreviewPortCommand(projectId, request.Port), ct);

        if (!result.IsSuccess)
        {
            Logger.LogWarning(
                "UpdatePreviewPort failed for project {ProjectId}: {Error}",
                projectId, result.Error);
            return BadRequest(new ProblemDetails
            {
                Title = "Could not update preview port",
                Detail = result.Error,
            });
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Update the project's default <b>runtime spec</b> — CPU class, vCPU count,
    /// RAM and volume size. The new spec applies to <i>subsequent</i> runtime
    /// rows only (new branch, fork, attach, AI onboarding); live runtimes keep
    /// the spec they booted with thanks to the snapshot pattern in the
    /// creation handlers. Powers the "Performance" tab in project settings so
    /// users can lab with different sizes per project.
    ///
    /// <para><b>Validation.</b> Delegated to <c>Project.SetRuntimeSpec(...)</c>.
    /// Sentinel error codes mapped to 400 so the frontend can render a
    /// targeted message:
    /// <list type="bullet">
    ///   <item><c>invalid_cpu_kind</c> — not "shared" or "performance".</item>
    ///   <item><c>invalid_cpu_count</c> — not one of {1, 2, 4, 8, 16}.</item>
    ///   <item><c>invalid_memory_mb</c> — outside the allowed range.</item>
    ///   <item><c>invalid_volume_size_gb</c> — outside 1..500 GiB.</item>
    ///   <item><c>performance_memory_too_low</c> — Fly's
    ///         <c>memoryMb &gt;= 2048 * cpus</c> rule for the performance class.</item>
    /// </list></para>
    /// </summary>
    [HttpPatch("{projectId:guid}/runtime-spec")]
    [ProducesResponseType(typeof(UpdateProjectRuntimeSpecResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<UpdateProjectRuntimeSpecResponse>> UpdateRuntimeSpec(
        Guid projectId,
        [FromBody] UpdateRuntimeSpecRequest? request,
        CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        if (request is null)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid body",
                Detail = "Request body required.",
            });
        }

        var result = await Mediator.Send(
            new UpdateProjectRuntimeSpecCommand(
                ProjectId: projectId,
                CpuKind: request.CpuKind,
                Cpus: request.Cpus,
                MemoryMb: request.MemoryMb,
                VolumeSizeGb: request.VolumeSizeGb),
            ct);

        if (!result.IsSuccess)
        {
            Logger.LogWarning(
                "UpdateRuntimeSpec failed for project {ProjectId}: {Error}",
                projectId, result.Error);
            return BadRequest(new ProblemDetails
            {
                Title = "Could not update runtime spec",
                Detail = result.Error,
            });
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Set (or clear) the project's <b>default Cursor model</b>. Mirrors
    /// <see cref="UpdateOpencodeModel"/> exactly: <c>null</c> clears the
    /// project default and lets the daemon's <c>CursorFactory</c> pick
    /// (<c>"auto"</c>); a non-null id must reference an active
    /// <c>CursorModels</c> row. Used only when the project's
    /// <see cref="Models.Project.AgentBackend"/> is <c>"cursor"</c>, but
    /// settable any time — the frontend keeps all three backends' picker
    /// state stable across radio flips.
    ///
    /// <para><b>Validation.</b> Same shape as <see cref="UpdateOpencodeModel"/>:
    /// 400 with <c>invalid_cursor_model</c> on a bad id, 404 on a missing
    /// project.</para>
    /// </summary>
    [HttpPatch("{projectId:guid}/cursor-model")]
    [ProducesResponseType(typeof(ProjectDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProjectDto>> UpdateCursorModel(
        Guid projectId,
        [FromBody] UpdateProjectCursorModelRequest? request,
        CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        if (request is null)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid body",
                Detail = "Request body required.",
            });
        }

        var result = await Mediator.Send(
            new UpdateProjectCursorModelCommand(
                ProjectId: projectId,
                ModelId: request.ModelId),
            ct);

        if (!result.IsSuccess)
        {
            var error = result.Error ?? string.Empty;

            if (error.StartsWith(UpdateProjectCursorModelHandler.NotFoundPrefix, StringComparison.Ordinal))
            {
                Logger.LogInformation(
                    "UpdateProjectCursorModel: 404 for project {ProjectId} caller {UserId}: {Error}",
                    projectId, userId, error);
                return NotFound();
            }

            Logger.LogWarning(
                "UpdateProjectCursorModel failed for project {ProjectId}: {Error}",
                projectId, error);
            return BadRequest(new ProblemDetails
            {
                Title = "Could not update cursor model",
                Detail = error,
            });
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Soft-delete a project. The row stays in the database with
    /// <c>IsDeleted=true</c> + auto-stamped <c>DeletedAt</c> / <c>DeletedBy</c>
    /// and is hidden from every subsequent query by the global
    /// <c>!IsDeleted</c> filter. Workspace-Admin (or higher) gate — same 404
    /// collapse as <see cref="Rename"/>. Returns 204 on success. Side-effects
    /// (stop runtimes, etc.) follow the <c>ProjectDeleted</c> domain event in
    /// follow-up work.
    /// </summary>
    [HttpDelete("{projectId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Delete(Guid projectId)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var result = await Mediator.Send(new DeleteProjectCommand(
            ProjectId: projectId,
            CallerUserId: userId));

        if (!result.IsSuccess)
        {
            if (result.Error?.StartsWith(DeleteProjectHandler.NotFoundPrefix, StringComparison.Ordinal) == true)
            {
                Logger.LogInformation(
                    "DeleteProject: 404 for project {ProjectId} caller {UserId}: {Error}",
                    projectId, userId, result.Error);
                return NotFound();
            }

            Logger.LogWarning(
                "DeleteProject failed for project {ProjectId}: {Error}",
                projectId, result.Error);
            return BadRequest(new { error = result.Error });
        }

        return NoContent();
    }

    /// <summary>
    /// "Bring your own key" mutation for a project's per-project Anthropic
    /// credentials. Owner-only: a non-owner gets a 404 (the spec is "don't
    /// leak project existence", same gate as <c>ProjectSecretsController</c>).
    ///
    /// <para><b>Tri-state body shape.</b> JSON cannot natively distinguish
    /// "field absent" from "field present with null value", so we encode the
    /// three states (leave alone / clear / set) explicitly with two booleans
    /// + two values per slot:</para>
    /// <code>
    /// {
    ///   "setAnthropicApiKey":      false,   // leave alone — value ignored
    ///   "anthropicApiKey":         null,
    ///   "setClaudeCodeOAuthToken": true,    // present — value either string or null
    ///   "claudeCodeOAuthToken":    "sk-..."
    /// }
    /// </code>
    /// <para>Within a single field: <c>setX=false</c> ignores <c>x</c> entirely;
    /// <c>setX=true</c> + <c>x=null</c> clears the slot; <c>setX=true</c> + a
    /// non-null string encrypts and persists.</para>
    ///
    /// <para>Plaintext travels exactly once on the request body and never
    /// echoes back — the response only carries presence booleans for each
    /// slot. The actual encryption is done by <c>SecretEncryptionService</c>
    /// using the project's DEK; the resulting envelope is stored on the
    /// <c>Projects</c> row.</para>
    /// </summary>
    [HttpPost("{projectId:guid}/byok")]
    [ProducesResponseType(typeof(UpdateProjectByokResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UpdateProjectByokResponse>> UpdateByok(
        Guid projectId,
        [FromBody] UpdateProjectByokRequest? request)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        if (request is null)
        {
            return BadRequest(new { error = "invalid_body" });
        }

        var cursor = request.SetCursorApiKey
            ? new OptionalSecret(IsSet: true, Value: request.CursorApiKey)
            : OptionalSecret.Unchanged();

        var result = await Mediator.Send(new UpdateProjectByokCommand(
            ProjectId: projectId,
            CallingUserId: userId,
            CursorApiKey: cursor));

        if (!result.IsSuccess)
        {
            // not-found: prefix → 404 (don't leak project existence). Other
            // errors are validation failures and surface as 400.
            if (result.Error?.StartsWith(UpdateProjectByokHandler.NotFoundPrefix, StringComparison.Ordinal) == true)
            {
                Logger.LogInformation(
                    "UpdateByok: 404 for project {ProjectId} caller {UserId}: {Error}",
                    projectId, userId, result.Error);
                return NotFound();
            }

            if (result.Error == UpdateProjectByokHandler.ProjectOverrideDisabled)
            {
                return BadRequest(new { error = result.Error });
            }

            Logger.LogWarning("UpdateByok validation failed: {Error}", result.Error);
            return BadRequest(new { error = result.Error });
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Fork an existing branch's runtime into a brand-new branch on the same
    /// project. The new branch points at the source branch's tip commit on
    /// GitHub and boots from a Fly volume that was forked block-for-block from
    /// the source — so local Postgres, the checked-out repo, Redis state and
    /// every toolchain cache come along for the ride. The source stays Online
    /// throughout (zero downtime).
    ///
    /// <para>Returns <c>202 Accepted</c> rather than 200/201 because the new
    /// runtime is in <c>Pending</c> state at response time; the recurring
    /// <c>RuntimeProvisionerJob</c> walks it to <c>Online</c> on its next
    /// tick. Frontends should subscribe to the existing SignalR
    /// runtime-state channel for live state transitions.</para>
    ///
    /// <para><b>Auth.</b> Caller must be a member of the project's workspace.
    /// Non-members and missing source branches both collapse to existence-safe
    /// 404 / 403; see the handler sentinels.</para>
    ///
    /// <para><b>Naming.</b> If <see cref="CopyBranchRequest.Name"/> is null or
    /// empty the handler auto-suffixes (<c>{source}-copy</c>, then
    /// <c>-copy-2</c>, <c>-copy-3</c>, …). An explicit name that collides with
    /// an existing branch is rejected with 400 before any GitHub or Fly
    /// resources are touched.</para>
    ///
    /// <para><b>Transactionality.</b> The orchestrator pre-flights the source
    /// on GitHub, creates the new ref, forks the volume, then commits the new
    /// branch + runtime rows in a single SaveChanges. Any failure tears the
    /// previously-completed steps down in reverse and the user sees a clean
    /// "nothing was changed" error.</para>
    /// </summary>
    [HttpPost("{projectId:guid}/branches/{branchId:guid}/copy")]
    [ProducesResponseType(typeof(CopyBranchResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<CopyBranchResponse>> CopyBranch(
        Guid projectId,
        Guid branchId,
        [FromBody] CopyBranchRequest? request,
        CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        // The body is optional — an empty POST means "auto-suffix the name"
        // AND "carry source spec" (the workspace-spec-catalog Scene 9 default).
        // Treat null exactly like an empty Name field + null spec picker fields.
        var requestedName = request?.Name;
        var catalogSpecId = request?.CatalogSpecId;
        var forceBlankSpec = request?.ForceBlankSpec ?? false;

        var result = await Mediator.Send(new CopyBranchCommand(
            SourceBranchId: branchId,
            NewBranchName: requestedName,
            CallerUserId: userId,
            CatalogSpecId: catalogSpecId,
            ForceBlankSpec: forceBlankSpec), ct);

        if (!result.IsSuccess)
        {
            // Sentinel-prefixed errors map to specific HTTP statuses surfaced
            // through ASP.NET Core's standard ProblemDetails shape so the
            // frontend can read .detail / .title uniformly. Bare strings (no
            // sentinel) fall through to a 503 — "we couldn't finish, nothing
            // was changed" — same body shape.
            var error = result.Error ?? string.Empty;

            if (error.StartsWith(CopyBranchHandler.ForbiddenPrefix, StringComparison.Ordinal))
            {
                Logger.LogWarning(
                    "CopyBranch: 403 for source branch {BranchId} caller {UserId}: {Error}",
                    branchId, userId, error);
                var detail = error.Substring(CopyBranchHandler.ForbiddenPrefix.Length).TrimStart();
                return Problem(detail: detail, statusCode: StatusCodes.Status403Forbidden, title: "Forbidden");
            }

            if (error.StartsWith(CopyBranchHandler.NotFoundPrefix, StringComparison.Ordinal))
            {
                Logger.LogInformation(
                    "CopyBranch: 404 for source branch {BranchId} caller {UserId}: {Error}",
                    branchId, userId, error);
                var detail = error.Substring(CopyBranchHandler.NotFoundPrefix.Length).TrimStart();
                return Problem(detail: detail, statusCode: StatusCodes.Status404NotFound, title: "Not found");
            }

            if (error.StartsWith(CopyBranchHandler.SourceNotPushedPrefix, StringComparison.Ordinal))
            {
                Logger.LogInformation(
                    "CopyBranch: 409 for source branch {BranchId} caller {UserId}: {Error}",
                    branchId, userId, error);
                var detail = error.Substring(CopyBranchHandler.SourceNotPushedPrefix.Length).TrimStart();
                return Problem(detail: detail, statusCode: StatusCodes.Status409Conflict, title: "Source branch not pushed");
            }

            if (error.StartsWith(CopyBranchHandler.NameConflictPrefix, StringComparison.Ordinal))
            {
                Logger.LogInformation(
                    "CopyBranch: 422 for source branch {BranchId} caller {UserId}: {Error}",
                    branchId, userId, error);
                var detail = error.Substring(CopyBranchHandler.NameConflictPrefix.Length).TrimStart();
                return Problem(detail: detail, statusCode: StatusCodes.Status422UnprocessableEntity, title: "Branch name conflict");
            }

            // workspace-spec-catalog: the caller asked to stamp a catalog
            // spec, but the id didn't resolve (or belongs to a different
            // workspace). Existence-safe 404 mirroring how we handle a missing
            // source branch — a member of workspace A must not be able to
            // probe whether catalog id X exists in workspace B.
            if (string.Equals(error, CopyBranchHandler.CatalogSpecNotFoundError, StringComparison.Ordinal))
            {
                Logger.LogInformation(
                    "CopyBranch: 404 for source branch {BranchId} caller {UserId}: catalog spec not found",
                    branchId, userId);
                return NotFound(new { error });
            }

            // cloudflare-tunnel-preview Phase 3: pool_empty is resource
            // exhaustion. Return a stable JSON body { error: "pool_empty" }
            // here (not ProblemDetails) so the frontend can switch on the
            // error string uniformly across create-project AND copy-branch
            // entry points. 409 Conflict — the request is well-formed, but
            // pool state blocks it.
            if (string.Equals(error, CopyBranchHandler.PoolEmptyError, StringComparison.Ordinal))
            {
                Logger.LogWarning(
                    "CopyBranch: 409 for source branch {BranchId} caller {UserId}: pool_empty",
                    branchId, userId);
                return Conflict(new { error });
            }

            Logger.LogWarning(
                "CopyBranch failed for source branch {BranchId}: {Error}",
                branchId, error);
            return Problem(detail: error, statusCode: StatusCodes.Status503ServiceUnavailable, title: "Copy branch failed");
        }

        var v = result.Value;
        // 202 — the runtime is Pending; the provisioner will boot it
        // asynchronously. Frontend tracks Online via SignalR runtime-state.
        return Accepted(new CopyBranchResponse(
            NewBranchId: v.NewBranchId,
            NewRuntimeId: v.NewRuntimeId,
            NewBranchName: v.NewBranchName,
            State: RuntimeState.Pending));
    }

    /// <summary>
    /// Soft-archive a non-default branch. Idempotent — archiving an already-
    /// archived branch returns 204. The branch row stays in the database with
    /// <c>IsArchived=true</c> + an <c>ArchivedAt</c> stamp, and is hidden from
    /// sidebars / branch pickers (<c>GET /branches</c> filters archived rows
    /// by default; pass <c>?includeArchived=true</c> to include them — used by
    /// the Settings → Branches tab). Past conversations on the branch remain
    /// readable.
    ///
    /// <para><b>Refusals.</b></para>
    /// <list type="bullet">
    ///   <item><c>is_default</c> (400) — the project's default branch cannot be
    ///         archived. Pick a non-default branch.</item>
    ///   <item><c>has_running_session</c> (400) — a turn is still in flight on
    ///         the branch. Stop the running turn first.</item>
    ///   <item><c>not_found</c> (404) — branch missing or not on this project.</item>
    /// </list>
    ///
    /// <para><b>Runtime side-effect.</b> If the branch's active runtime is
    /// running-ish (Online / Booting / Bootstrapping / Waking) it's walked
    /// to <c>Suspending</c> in the same SaveChanges; the reconciler then asks
    /// Fly to stop the machine. Already-suspended / failed runtimes are left
    /// alone.</para>
    /// </summary>
    [HttpPost("{projectId:guid}/branches/{branchId:guid}/archive")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> ArchiveBranch(
        Guid projectId,
        Guid branchId,
        CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var result = await Mediator.Send(
            new ArchiveBranchCommand(ProjectId: projectId, BranchId: branchId), ct);

        if (!result.IsSuccess)
        {
            var error = result.Error ?? string.Empty;

            if (string.Equals(error, ArchiveBranchHandler.NotFoundError, StringComparison.Ordinal))
            {
                Logger.LogInformation(
                    "ArchiveBranch: 404 for branch {BranchId} on project {ProjectId} caller {UserId}",
                    branchId, projectId, userId);
                return NotFound(new { error });
            }

            if (string.Equals(error, ArchiveBranchHandler.IsDefaultError, StringComparison.Ordinal))
            {
                Logger.LogInformation(
                    "ArchiveBranch: 400 is_default for branch {BranchId} on project {ProjectId}",
                    branchId, projectId);
                return BadRequest(new
                {
                    error,
                    message = "The default branch cannot be archived.",
                });
            }

            if (string.Equals(error, ArchiveBranchHandler.HasRunningSessionError, StringComparison.Ordinal))
            {
                Logger.LogInformation(
                    "ArchiveBranch: 400 has_running_session for branch {BranchId} on project {ProjectId}",
                    branchId, projectId);
                return BadRequest(new
                {
                    error,
                    message = "Stop the running turn first.",
                });
            }

            Logger.LogWarning(
                "ArchiveBranch failed for branch {BranchId} on project {ProjectId}: {Error}",
                branchId, projectId, error);
            return BadRequest(new { error });
        }

        return NoContent();
    }

    /// <summary>
    /// Restore a previously-archived branch back to the active set. Idempotent
    /// — unarchiving an already-active branch returns 204. Does NOT touch the
    /// runtime; it wakes naturally on the next user interaction via the
    /// existing wake-on-connect path.
    /// </summary>
    [HttpPost("{projectId:guid}/branches/{branchId:guid}/unarchive")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> UnarchiveBranch(
        Guid projectId,
        Guid branchId,
        CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var result = await Mediator.Send(
            new UnarchiveBranchCommand(ProjectId: projectId, BranchId: branchId), ct);

        if (!result.IsSuccess)
        {
            var error = result.Error ?? string.Empty;

            if (string.Equals(error, UnarchiveBranchHandler.NotFoundError, StringComparison.Ordinal))
            {
                Logger.LogInformation(
                    "UnarchiveBranch: 404 for branch {BranchId} on project {ProjectId} caller {UserId}",
                    branchId, projectId, userId);
                return NotFound(new { error });
            }

            Logger.LogWarning(
                "UnarchiveBranch failed for branch {BranchId} on project {ProjectId}: {Error}",
                branchId, projectId, error);
            return BadRequest(new { error });
        }

        return NoContent();
    }

    /// <summary>
    /// Claim a fresh preview subdomain from the Cloudflare pool and bind it to
    /// an existing branch that doesn't have one yet. The recovery path for
    /// legacy branches that pre-date the cloudflare-tunnel-preview Phase 3
    /// pool — their <c>AssignedSubdomain</c> is null, so the Preview tab
    /// renders the empty "No preview subdomain yet" state. The user clicks
    /// "Assign subdomain" → this endpoint → the branch's
    /// <see cref="ProjectBranchDto.PreviewHostname"/> is populated and the
    /// iframe goes live.
    ///
    /// <para><b>Refusals.</b></para>
    /// <list type="bullet">
    ///   <item><c>not_found</c> (404) — project or branch missing, or the
    ///         caller isn't a member of the workspace.</item>
    ///   <item><c>already_assigned</c> (409) — the branch already has a
    ///         subdomain. The frontend's correct response is to invalidate
    ///         its branches cache and stop showing the button.</item>
    ///   <item><c>pool_empty</c> (409) — the pool is exhausted; ask an admin
    ///         to batch-create more.</item>
    /// </list>
    /// </summary>
    [HttpPost("{projectId:guid}/branches/{branchId:guid}/assign-subdomain")]
    [ProducesResponseType(typeof(ProjectBranchDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProjectBranchDto>> AssignBranchSubdomain(
        Guid projectId,
        Guid branchId,
        CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var result = await Mediator.Send(
            new AssignBranchSubdomainCommand(
                CallerUserId: userId,
                ProjectId: projectId,
                BranchId: branchId),
            ct);

        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        var error = result.Error ?? string.Empty;

        if (string.Equals(error, AssignBranchSubdomainHandler.NotFoundError, StringComparison.Ordinal))
        {
            Logger.LogInformation(
                "AssignBranchSubdomain: 404 for branch {BranchId} on project {ProjectId} caller {UserId}",
                branchId, projectId, userId);
            return NotFound(new { error });
        }

        if (string.Equals(error, AssignBranchSubdomainHandler.AlreadyAssignedError, StringComparison.Ordinal))
        {
            Logger.LogInformation(
                "AssignBranchSubdomain: 409 already_assigned for branch {BranchId}",
                branchId);
            return Conflict(new
            {
                error,
                message = "This branch already has a preview subdomain.",
            });
        }

        if (string.Equals(error, AssignBranchSubdomainHandler.PoolEmptyError, StringComparison.Ordinal))
        {
            Logger.LogWarning(
                "AssignBranchSubdomain: 409 pool_empty for branch {BranchId}",
                branchId);
            return Conflict(new
            {
                error,
                message = "Preview subdomain pool is empty. Ask an admin to batch-create more.",
            });
        }

        Logger.LogWarning(
            "AssignBranchSubdomain failed for branch {BranchId} on project {ProjectId}: {Error}",
            branchId, projectId, error);
        return BadRequest(new { error });
    }

    /// <summary>
    /// Live list of git branches on the project's GitHub repo, enriched with the id of
    /// the matching system <see cref="ProjectBranch"/> when one exists (1:1 by branch
    /// name). Drives the "New Session" branch picker — the frontend renders an "Open"
    /// action for git branches that already have a system branch
    /// (<see cref="GithubBranchListItemDto.LinkedSystemBranchId"/> non-null) and an
    /// "Attach / Fork" action for the rest.
    ///
    /// <para><b>Auth.</b> Caller must be a member of the project's workspace.
    /// Non-members and missing/soft-deleted projects both collapse to 404 — same
    /// existence-safe gate as <see cref="Get"/> and <see cref="ListBranches"/>.</para>
    ///
    /// <para><b>Round-trips.</b> One DB read for the project, one for the membership
    /// gate, one for the local branch index — plus two live GitHub calls
    /// (<c>GET /repos/{owner}/{repo}</c> + <c>GET /repos/{owner}/{repo}/branches</c>).
    /// No N+1.</para>
    /// </summary>
    [HttpGet("{projectId:guid}/github-branches")]
    [ProducesResponseType(typeof(List<GithubBranchListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<List<GithubBranchListItemDto>>> ListGithubBranches(
        Guid projectId,
        CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var result = await Mediator.Send(new ListProjectGithubBranchesQuery(
            ProjectId: projectId,
            CallerUserId: userId), ct);

        if (!result.IsSuccess)
        {
            if (result.Error?.StartsWith(ListProjectGithubBranchesHandler.NotFoundPrefix, StringComparison.Ordinal) == true)
            {
                Logger.LogInformation(
                    "ListProjectGithubBranches: 404 for project {ProjectId} caller {UserId}: {Error}",
                    projectId, userId, result.Error);
                return NotFound();
            }

            Logger.LogWarning("ListProjectGithubBranches failed: {Error}", result.Error);
            return BadRequest(new { error = result.Error });
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// "Continue working on this git branch" — links an existing git branch on the
    /// project's repo as a new system <see cref="ProjectBranch"/> + <see cref="ProjectRuntime"/>.
    /// No new git ref is pushed; the runtime boots from a fresh Fly volume that the
    /// daemon clones the git branch into on first start. Slow path counterpart to
    /// <see cref="CopyBranch"/> — used when only a git branch exists.
    ///
    /// <para>Returns <c>202 Accepted</c> — the new runtime is in
    /// <see cref="RuntimeState.Pending"/> at response time; the recurring
    /// <c>RuntimeProvisionerJob</c> walks it forward. Frontends subscribe to the
    /// existing SignalR runtime-state channel to observe the transition.</para>
    /// </summary>
    [HttpPost("{projectId:guid}/branches/attach")]
    [ProducesResponseType(typeof(AttachGitBranchResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AttachGitBranchResponse>> AttachBranch(
        Guid projectId,
        [FromBody] AttachGitBranchRequest? request,
        CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        if (request is null || string.IsNullOrWhiteSpace(request.GitBranchName))
        {
            return BadRequest(new { error = "invalid_body" });
        }

        var result = await Mediator.Send(new AttachGitBranchCommand(
            ProjectId: projectId,
            GitBranchName: request.GitBranchName,
            CallerUserId: userId), ct);

        if (!result.IsSuccess)
        {
            var error = result.Error ?? string.Empty;

            if (error.StartsWith(AttachGitBranchHandler.NotFoundPrefix, StringComparison.Ordinal))
            {
                Logger.LogInformation(
                    "AttachGitBranch: 404 for project {ProjectId} caller {UserId}: {Error}",
                    projectId, userId, error);
                return NotFound();
            }

            if (string.Equals(error, AttachGitBranchHandler.GitBranchNotFoundError, StringComparison.Ordinal))
            {
                Logger.LogInformation(
                    "AttachGitBranch: 404 for project {ProjectId} branch {Branch} caller {UserId}: git branch not found",
                    projectId, request.GitBranchName, userId);
                return NotFound(new { error });
            }

            if (string.Equals(error, AttachGitBranchHandler.AlreadyLinkedError, StringComparison.Ordinal))
            {
                Logger.LogInformation(
                    "AttachGitBranch: 409 for project {ProjectId} branch {Branch}: BranchAlreadyLinked",
                    projectId, request.GitBranchName);
                return Conflict(new { error });
            }

            if (string.Equals(error, AttachGitBranchHandler.PoolEmptyError, StringComparison.Ordinal))
            {
                Logger.LogWarning(
                    "AttachGitBranch: 409 for project {ProjectId} branch {Branch}: pool_empty",
                    projectId, request.GitBranchName);
                return Conflict(new { error });
            }

            Logger.LogWarning(
                "AttachGitBranch failed for project {ProjectId} branch {Branch}: {Error}",
                projectId, request.GitBranchName, error);
            return BadRequest(new { error });
        }

        var v = result.Value;
        return Accepted(new AttachGitBranchResponse(
            BranchId: v.BranchId,
            RuntimeId: v.RuntimeId,
            State: v.State));
    }

    /// <summary>
    /// "Create a new branch based on this git branch" — pushes a new git ref forked
    /// from the source's HEAD SHA, then provisions a fresh system
    /// <see cref="ProjectBranch"/> + <see cref="ProjectRuntime"/> for it. Slow path
    /// counterpart to <see cref="CopyBranch"/> — used when only a git branch exists
    /// (no source system branch / volume to fork).
    ///
    /// <para>Returns <c>202 Accepted</c>. The new runtime is in
    /// <see cref="RuntimeState.Pending"/> at response time; the recurring
    /// <c>RuntimeProvisionerJob</c> walks it forward to <c>Online</c>.</para>
    /// </summary>
    [HttpPost("{projectId:guid}/branches/fork-from-git")]
    [ProducesResponseType(typeof(ForkBranchFromGitResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ForkBranchFromGitResponse>> ForkBranchFromGit(
        Guid projectId,
        [FromBody] ForkBranchFromGitRequest? request,
        CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        if (request is null ||
            string.IsNullOrWhiteSpace(request.SourceGitBranchName) ||
            string.IsNullOrWhiteSpace(request.NewBranchName))
        {
            return BadRequest(new { error = "invalid_body" });
        }

        var result = await Mediator.Send(new ForkBranchFromGitCommand(
            ProjectId: projectId,
            SourceGitBranchName: request.SourceGitBranchName,
            NewBranchName: request.NewBranchName,
            CallerUserId: userId,
            CatalogSpecId: request.CatalogSpecId,
            ForceBlankSpec: request.ForceBlankSpec), ct);

        if (!result.IsSuccess)
        {
            var error = result.Error ?? string.Empty;

            if (error.StartsWith(ForkBranchFromGitHandler.NotFoundPrefix, StringComparison.Ordinal))
            {
                Logger.LogInformation(
                    "ForkBranchFromGit: 404 for project {ProjectId} caller {UserId}: {Error}",
                    projectId, userId, error);
                return NotFound();
            }

            if (string.Equals(error, ForkBranchFromGitHandler.SourceGitBranchNotFoundError, StringComparison.Ordinal))
            {
                Logger.LogInformation(
                    "ForkBranchFromGit: 404 source branch {Source} not found on project {ProjectId}",
                    request.SourceGitBranchName, projectId);
                return NotFound(new { error });
            }

            if (string.Equals(error, ForkBranchFromGitHandler.InvalidBranchNameError, StringComparison.Ordinal))
            {
                Logger.LogInformation(
                    "ForkBranchFromGit: 400 invalid new branch name {New} on project {ProjectId}",
                    request.NewBranchName, projectId);
                return BadRequest(new { error });
            }

            if (string.Equals(error, ForkBranchFromGitHandler.NameConflictError, StringComparison.Ordinal))
            {
                Logger.LogInformation(
                    "ForkBranchFromGit: 409 name conflict {New} on project {ProjectId}",
                    request.NewBranchName, projectId);
                return Conflict(new { error });
            }

            if (string.Equals(error, ForkBranchFromGitHandler.PoolEmptyError, StringComparison.Ordinal))
            {
                Logger.LogWarning(
                    "ForkBranchFromGit: 409 pool_empty for project {ProjectId} new branch {New}",
                    projectId, request.NewBranchName);
                return Conflict(new { error });
            }

            if (string.Equals(error, ForkBranchFromGitHandler.CatalogSpecNotFoundError, StringComparison.Ordinal))
            {
                Logger.LogInformation(
                    "ForkBranchFromGit: 404 catalog spec not found for project {ProjectId}",
                    projectId);
                return NotFound(new { error });
            }

            Logger.LogWarning(
                "ForkBranchFromGit failed for project {ProjectId} new branch {New}: {Error}",
                projectId, request.NewBranchName, error);
            return BadRequest(new { error });
        }

        var v = result.Value;
        return Accepted(new ForkBranchFromGitResponse(
            BranchId: v.BranchId,
            RuntimeId: v.RuntimeId,
            NewBranchName: v.NewBranchName,
            State: v.State));
    }
}

/// <summary>
/// Wire shape for <c>POST /api/projects/{projectId}/branches/{branchId}/copy</c>.
/// All fields are optional — an empty body asks the orchestrator to auto-suffix
/// the source branch's name with <c>-copy</c>, <c>-copy-2</c>, … and carry the
/// source runtime's spec verbatim (the workspace-spec-catalog Scene 9 default).
///
/// <para><b>Services / spec picker.</b> Mirrors Scene 2 of <c>workspace-spec-catalog</c>:
/// <list type="bullet">
///   <item>Both <see cref="CatalogSpecId"/> and <see cref="ForceBlankSpec"/>
///         omitted/null/false — carry the source runtime's <c>Spec</c>.</item>
///   <item><see cref="CatalogSpecId"/> set — stamp the named workspace catalog
///         entry onto the new runtime (deep copy at fork time).</item>
///   <item><see cref="ForceBlankSpec"/> = <c>true</c> — start the new runtime
///         with <c>Spec = NULL</c>.</item>
/// </list>
/// If both spec fields are supplied the blank flag wins; see the handler's
/// command record for the full priority rule.</para>
/// </summary>
public record CopyBranchRequest(
    string? Name = null,
    Guid? CatalogSpecId = null,
    bool ForceBlankSpec = false);

/// <summary>
/// Wire shape returned from the copy-branch endpoint. The runtime is in
/// <see cref="RuntimeState.Pending"/> at response time — the recurring
/// <c>RuntimeProvisionerJob</c> walks it forward to <c>Online</c>. Frontends
/// subscribe to the existing SignalR runtime-state channel to observe the
/// transition without polling.
/// </summary>
public record CopyBranchResponse(
    Guid NewBranchId,
    Guid NewRuntimeId,
    string NewBranchName,
    RuntimeState State);

/// <summary>
/// Body shape for <c>POST /api/projects/{projectId}/byok</c>. The two
/// <c>SetX</c> booleans + matching <c>X</c> value pairs encode the
/// "leave alone / clear / set" tri-state described on the controller method.
/// Defaults are all "false / null" so a totally-empty body is a valid no-op.
/// </summary>
public record UpdateProjectByokRequest(
    bool SetCursorApiKey = false,
    string? CursorApiKey = null);

/// <summary>
/// Body shape for <c>PATCH /api/projects/{projectId}</c>. All fields are
/// optional — null/omitted means "leave alone". Field-level validation lives
/// on the entity:
/// <list type="bullet">
///   <item><c>Name</c> → <c>Project.Rename(string)</c> (trim + non-empty +
///         length ≤ 100).</item>
///   <item><c>PreviewPort</c> → <c>Project.SetPreviewPort(int)</c>
///         (1..65535).</item>
/// </list>
/// </summary>
public record RenameProjectRequest(string? Name = null, int? PreviewPort = null);

/// <summary>
/// Body shape for <c>PATCH /api/projects/{projectId}/preview-port</c>. Single
/// required field — the new port. Validation (1..65535) happens on the entity
/// via <c>Project.SetPreviewPort(int)</c>; the controller does not pre-screen.
/// </summary>
public record UpdatePreviewPortRequest(int Port);

/// <summary>
/// Body shape for <c>PATCH /api/projects/{projectId}/runtime-spec</c>. Four
/// correlated fields; cross-field validation (performance class needs
/// <c>memoryMb &gt;= 2048 * cpus</c>) lives on
/// <c>Project.SetRuntimeSpec(...)</c> so the controller does not pre-screen.
/// </summary>
public record UpdateRuntimeSpecRequest(
    string CpuKind,
    int Cpus,
    int MemoryMb,
    int VolumeSizeGb);

/// <summary>
/// Body shape for <c>PATCH /api/projects/{projectId}/agent-model</c>. Single
/// optional field — <c>null</c> clears the project default and falls back to
/// the SDK's built-in model; a non-null id must reference an active
/// <c>AgentModels</c> row (validated in the handler).
/// </summary>
/// <summary>
/// Body shape for <c>PATCH /api/projects/{projectId}/agent-backend</c>. Single
/// required field — must be one of <c>Project.AllowedAgentBackends</c> (closed
/// set <c>{"claude", "opencode"}</c>). Validated in the handler against the
/// entity's invariant via <c>Project.SetDefaultAgentBackend(string)</c>; the
/// controller does not pre-screen.
/// </summary>
/// <summary>
/// Body shape for <c>PATCH /api/projects/{projectId}/opencode-model</c>.
/// Mirrors <see cref="UpdateProjectAgentModelRequest"/>: single optional field
/// — <c>null</c> clears the project default (daemon falls back to its
/// <c>OpencodeFactory</c>); a non-null id must reference an active
/// <c>OpencodeModels</c> row (validated in the handler).
/// </summary>
/// <summary>
/// Body shape for <c>PATCH /api/projects/{projectId}/cursor-model</c>.
/// Mirrors <see cref="UpdateProjectOpencodeModelRequest"/>: single optional
/// field — <c>null</c> clears the project default (daemon falls back to its
/// <c>CursorFactory</c>); a non-null id must reference an active
/// <c>CursorModels</c> row (validated in the handler).
/// </summary>
public record UpdateProjectCursorModelRequest(Guid? ModelId);

/// <summary>
/// Body shape for <c>POST /api/projects/{projectId}/branches/attach</c>. The
/// <c>GitBranchName</c> field is the existing git branch name on the project's repo —
/// it becomes the new system branch's <c>Name</c> 1:1 (same identity rule used by the
/// project-scoped GitHub branch list).
/// </summary>
public record AttachGitBranchRequest(string GitBranchName);

/// <summary>
/// Wire shape returned by <c>POST /api/projects/{projectId}/branches/attach</c>. The
/// runtime is in <see cref="RuntimeState.Pending"/> at response time — the recurring
/// <c>RuntimeProvisionerJob</c> walks it forward. Same shape pattern as
/// <see cref="CopyBranchResponse"/>, minus the redundant name (caller already knows it).
/// </summary>
public record AttachGitBranchResponse(
    Guid BranchId,
    Guid RuntimeId,
    RuntimeState State);

/// <summary>
/// Body shape for <c>POST /api/projects/{projectId}/branches/fork-from-git</c>.
/// The two name fields are required — there is no auto-suffix on this entry
/// point because the caller is explicitly creating a brand-new git ref and a
/// brand-new system branch in one motion.
///
/// <para><b>Services / spec picker.</b> Optional. Two-way pick (no "same as
/// source" — there's no source runtime on this entry point):
/// <list type="bullet">
///   <item>Both fields omitted/null/false — runtime starts blank (<c>Spec = NULL</c>).</item>
///   <item><see cref="CatalogSpecId"/> set — deep-copy the named workspace
///         catalog entry's content into the new runtime.</item>
/// </list>
/// <see cref="ForceBlankSpec"/> is accepted for symmetry with
/// <see cref="CopyBranchRequest"/> even though it equals the default.</para>
/// </summary>
public record ForkBranchFromGitRequest(
    string SourceGitBranchName,
    string NewBranchName,
    Guid? CatalogSpecId = null,
    bool ForceBlankSpec = false);

/// <summary>
/// Wire shape returned by <c>POST /api/projects/{projectId}/branches/fork-from-git</c>.
/// Mirrors <see cref="CopyBranchResponse"/> — the runtime is Pending and the new git
/// ref + system branch row are already in place.
/// </summary>
public record ForkBranchFromGitResponse(
    Guid BranchId,
    Guid RuntimeId,
    string NewBranchName,
    RuntimeState State);

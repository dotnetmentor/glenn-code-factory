using System.Security.Claims;
using System.Text.Json;
using Hangfire;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Source.Features.Cloudflare.Commands;
using Source.Features.Projects.Models;
using Source.Features.RuntimeLifecycle.Jobs;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;
using Source.Infrastructure.AuthorizationModels;

namespace Source.Features.RuntimeLifecycle.Controllers;

/// <summary>
/// User-facing entry point for the project-onboarding flow (Spec 16, Card 7).
/// Creates a fresh <see cref="Project"/>, default <see cref="ProjectBranch"/>
/// and <see cref="ProjectRuntime"/> in <see cref="RuntimeState.Pending"/> — the
/// row that the existing <c>RuntimeProvisionerJob</c> picks up to actually boot
/// the Fly machine. The optional <see cref="CreateProjectRuntimeRequest.InitialSpec"/>
/// is what differentiates the manual stack picker (spec set up front) from the
/// AI-curated flow (spec left null and filled in incrementally via proposals).
///
/// <para>The Project entity now exists in <c>Source.Features.Projects</c> with
/// a real FK from <see cref="ProjectRuntime.ProjectId"/>, so this handler must
/// persist a Project row alongside the runtime — otherwise the FK insert
/// blows up with 23503. Workspace + GitHub installation are looked up from the
/// caller's first available membership; repo coordinates default to placeholders
/// because the AI flow doesn't pick a repo up front (the agent chooses one
/// later via proposals).</para>
///
/// <para>Why this controller is thin. Mirrors <see cref="RuntimeAdminController"/> —
/// every action is a tiny EF write; wrapping in MediatR commands would add
/// boilerplate without behavior. The endpoint is SuperAdmin-only because Spec 16
/// onboarding currently lives in the super-admin UI and runtimes consume paid
/// infrastructure.</para>
/// </summary>
[ApiController]
[Route("api/admin/runtimes")]
[Authorize(Roles = RoleConstants.SuperAdmin)]
[Tags("RuntimeAdmin")]
public class RuntimeProvisionController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IMediator _mediator;
    private readonly IBackgroundJobClient _backgroundJobs;
    private readonly ILogger<RuntimeProvisionController> _logger;

    public RuntimeProvisionController(
        ApplicationDbContext db,
        IMediator mediator,
        IBackgroundJobClient backgroundJobs,
        ILogger<RuntimeProvisionController> logger)
    {
        _db = db;
        _mediator = mediator;
        _backgroundJobs = backgroundJobs;
        _logger = logger;
    }

    /// <summary>
    /// Provision a fresh <see cref="ProjectRuntime"/> in <see cref="RuntimeState.Pending"/>.
    /// The provisioner job will pick it up and walk it to <see cref="RuntimeState.Online"/>.
    /// <see cref="CreateProjectRuntimeRequest.InitialSpec"/> may be null (AI flow:
    /// empty runtime, spec is filled in by daemon proposals) or a fully-formed
    /// curation spec (manual flow: ship with the picked stack on day one).
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(CreateProjectRuntimeResponse), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(409)]
    public async Task<ActionResult<CreateProjectRuntimeResponse>> Create(
        [FromBody] CreateProjectRuntimeRequest request,
        CancellationToken ct)
    {
        // The InitialSpec field arrives as a JSON object in the request body but
        // we persist it as text in the jsonb column. Re-serialize to a stable
        // shape so we own the on-disk format. Null stays null — that's the
        // "AI-curated" flow signal (no spec yet).
        string? specJson = null;
        if (request.InitialSpec is not null)
        {
            specJson = JsonSerializer.Serialize(request.InitialSpec, JsonOpts);
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var region = string.IsNullOrWhiteSpace(request.Region) ? "arn" : request.Region.Trim();

        // -------- Resolve workspace + GitHub installation --------
        // The AI onboarding UI doesn't ask the user to pick a workspace or
        // installation; pick the first workspace the caller is a member of and
        // the first installation in that workspace. Mirrors the membership
        // lookup in CreateProjectHandler so the FKs we plant on the Project
        // row are guaranteed to resolve.
        var membership = await _db.WorkspaceMemberships
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new { m.WorkspaceId })
            .FirstOrDefaultAsync(ct);

        if (membership is null)
        {
            return BadRequest(new { error = "Caller is not a member of any workspace." });
        }

        var installationId = await _db.GithubInstallations
            .AsNoTracking()
            .Where(i => i.WorkspaceId == membership.WorkspaceId)
            .OrderBy(i => i.CreatedAt)
            .Select(i => (Guid?)i.Id)
            .FirstOrDefaultAsync(ct);

        if (installationId is null)
        {
            return BadRequest(new { error = "Workspace has no connected GitHub installation. Connect one before provisioning a runtime." });
        }

        // -------- Build Project + default branch + runtime --------
        // Mirrors CreateProjectHandler: three rows, one SaveChanges, project
        // raises ProjectCreated via MarkCreated() so the DomainEventInterceptor
        // dispatches downstream handlers (audit, telemetry).
        var projectName = DeriveProjectName(request.Name);

        var project = new Project
        {
            Id = Guid.NewGuid(),
            WorkspaceId = membership.WorkspaceId,
            OwnerUserId = userId,
            Name = projectName,
            // AI flow doesn't pick a repo up front — placeholders unblock the
            // FK and the agent picks a real repo via proposals later.
            GithubRepoOwner = "pending",
            GithubRepoName = "pending",
            GithubInstallationId = installationId.Value,
            // Manual flow ships with the picked stack on day one (specJson
            // non-null); AI flow leaves Spec null and the daemon proposes
            // services incrementally. Per `project-level-runtime-spec` this
            // lives on Project, not ProjectRuntime; SpecVersion bumps from 1
            // to 2 if a non-null body lands (V2-shape JSON by construction).
            Spec = specJson,
            SpecVersion = specJson is null ? 1 : 2,
        };

        var branch = new ProjectBranch
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Name = "main",
            IsDefault = true,
        };

        var runtime = new ProjectRuntime
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            BranchId = branch.Id,
            // TenantId carries the workspace boundary all the way to the
            // daemon's rt_tenant JWT claim — same as CreateProjectHandler.
            TenantId = membership.WorkspaceId,
            State = RuntimeState.Pending,
            StateChangedAt = DateTime.UtcNow,
            Region = region,
            // Snapshot the project's (just-created) runtime MACHINE spec onto
            // the runtime row. The runtime SERVICES spec lives on Project per
            // `project-level-runtime-spec` and is read at bootstrap time.
            CpuKind = project.RuntimeCpuKind,
            Cpus = project.RuntimeCpus,
            MemoryMb = project.RuntimeMemoryMb,
            VolumeSizeGb = project.RuntimeVolumeSizeGb,
        };

        _db.Projects.Add(project);
        _db.ProjectBranches.Add(branch);
        _db.ProjectRuntimes.Add(runtime);

        // Raise ProjectCreated — handlers run after SaveChanges via DomainEventInterceptor.
        project.MarkCreated();

        // cloudflare-tunnel-preview Phase 3: claim a preview subdomain for
        // the new branch atomically with the DB writes. Wrap in an explicit
        // transaction so AssignSubdomainToBranchHandler defers its SaveChanges
        // to ours — Project + Branch + Runtime + claimed subdomain commit
        // together, or none do.
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var assignResult = await _mediator.Send(
            new Source.Features.Cloudflare.Commands.AssignSubdomainToBranchCommand(branch.Id),
            ct);

        if (!assignResult.IsSuccess)
        {
            await tx.RollbackAsync(ct);
            _logger.LogWarning(
                "RuntimeProvision: subdomain claim failed for new branch {BranchId}: {Error}",
                branch.Id, assignResult.Error);
            // pool_empty → 409 with body { error: "pool_empty" } so the
            // frontend can surface the spec's "ask your admin to batch-create
            // more" message. Anything else falls through to 400.
            if (string.Equals(assignResult.Error, "pool_empty", StringComparison.Ordinal))
            {
                return Conflict(new { error = assignResult.Error });
            }
            return BadRequest(new { error = assignResult.Error ?? "subdomain_claim_failed" });
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // Fire the provisioner immediately for this new Pending row so the
        // user doesn't wait up to a minute for the recurring sweep. The
        // minutely sweep stays in place as a safety net for the rare race
        // where the row commits but this enqueue doesn't (process killed
        // between CommitAsync and Enqueue). ProvisionOne re-checks the
        // Pending state at the top so a double-fire from sweep + ad-hoc
        // simply no-ops on the loser.
        _backgroundJobs.Enqueue<RuntimeProvisionerJob>(
            j => j.ProvisionOne(runtime.Id, JobCancellationToken.Null));

        _logger.LogInformation(
            "User {UserId} provisioned project {ProjectId} runtime {RuntimeId} in workspace {WorkspaceId} (specPresent={SpecPresent}, name={Name})",
            userId, project.Id, runtime.Id, membership.WorkspaceId, specJson is not null, project.Name);

        return CreatedAtAction(
            nameof(RuntimeAdminController.GetById),
            controllerName: "RuntimeAdmin",
            routeValues: new { id = runtime.Id },
            value: new CreateProjectRuntimeResponse(
                ProjectId: project.Id,
                RuntimeId: runtime.Id,
                State: runtime.State));
    }

    /// <summary>
    /// Pick a sensible display name for the new project. The AI onboarding form
    /// doesn't ask for a name explicitly; if the caller passes one we use it
    /// (trimmed + capped to the 200-char column limit), otherwise we fall back
    /// to "Untitled project".
    /// </summary>
    private static string DeriveProjectName(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var trimmed = requested.Trim();
            return trimmed.Length > 200 ? trimmed[..200] : trimmed;
        }

        return "Untitled project";
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        // Match the camelCase shape used elsewhere in the curation flow
        // (see CreateRuntimeProposalCommandHandler) so on-disk specs are
        // consistent across producers.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}

/// <summary>
/// Wire shape for <c>POST /api/admin/runtimes</c>. Both onboarding flows post
/// here:
/// <list type="bullet">
///   <item><b>Manual flow</b> sends <see cref="InitialSpec"/> with a fully-formed
///         <c>{ languages, services, extras }</c> body so the runtime ships with
///         the picked stack.</item>
///   <item><b>AI flow</b> leaves <see cref="InitialSpec"/> null — the runtime
///         boots empty and the daemon's <c>propose_runtime_spec</c> tool fills
///         it in incrementally.</item>
/// </list>
/// </summary>
public record CreateProjectRuntimeRequest(
    string? Name,
    string? Region,
    InitialRuntimeSpec? InitialSpec);

/// <summary>
/// Initial curation spec for a new runtime. Mirrors the on-disk jsonb shape
/// (<c>{ languages, services, extras }</c>) so the manual stack picker can hand
/// it through unchanged. Backend stores this verbatim as text in the
/// <c>ProjectRuntime.Spec</c> jsonb column.
/// </summary>
public record InitialRuntimeSpec(
    IReadOnlyList<string>? Languages,
    IReadOnlyList<string>? Services,
    IReadOnlyList<string>? Extras);

/// <summary>
/// Wire shape returned to the onboarding UI. The frontend uses
/// <see cref="ProjectId"/> to navigate into the project workspace.
/// </summary>
public record CreateProjectRuntimeResponse(
    Guid ProjectId,
    Guid RuntimeId,
    RuntimeState State);

using Microsoft.EntityFrameworkCore;
using Source.Features.Projects.AgentPermissions.Models;
using Source.Features.Projects.Models;
using Source.Features.Workspaces.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.AgentPermissions.Commands.UpsertProjectAgentPermissions;

/// <summary>
/// Handler for <see cref="UpsertProjectAgentPermissionsCommand"/>. Single
/// transaction: gate by workspace membership, validate the SDK-shaped fields,
/// upsert the override row, save.
///
/// <para><b>Error shape.</b> "Project missing / not a member" → a
/// <see cref="NotFoundPrefix"/>-prefixed failure so the controller maps to 404
/// (same "don't leak existence" gate as <c>GetProjectHandler</c>). Validation
/// failures (invalid mode, bypass-without-skip) use bare error strings the
/// controller maps to 400 — these are user-facing messages the settings UI
/// can surface verbatim.</para>
/// </summary>
public sealed class UpsertProjectAgentPermissionsHandler
    : ICommandHandler<UpsertProjectAgentPermissionsCommand, Result<ProjectAgentPermissionsDto>>
{
    /// <summary>
    /// Sentinel prefix the controller matches on to map "no project / no
    /// access" to 404. Mirrors <c>GetProjectHandler.NotFoundPrefix</c>.
    /// </summary>
    public const string NotFoundPrefix = "not-found:";

    /// <summary>
    /// Allowed values for <see cref="ProjectAgentPermissions.PermissionMode"/>.
    /// Matches the SDK enum minus <c>auto</c>, which the spec's Non-Goals
    /// excludes. Case-sensitive on purpose — the SDK is case-sensitive, and
    /// permissive matching would let a typo silently downgrade to
    /// <c>default</c>.
    /// </summary>
    private static readonly HashSet<string> AllowedModes = new(StringComparer.Ordinal)
    {
        "default",
        "acceptEdits",
        "bypassPermissions",
        "plan",
        "dontAsk",
    };

    private const string BypassMode = "bypassPermissions";

    private readonly ApplicationDbContext _db;
    private readonly ILogger<UpsertProjectAgentPermissionsHandler> _logger;

    public UpsertProjectAgentPermissionsHandler(
        ApplicationDbContext db,
        ILogger<UpsertProjectAgentPermissionsHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Result<ProjectAgentPermissionsDto>> Handle(
        UpsertProjectAgentPermissionsCommand request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CallerUserId))
        {
            return Result.Failure<ProjectAgentPermissionsDto>(
                $"{NotFoundPrefix} unauthenticated");
        }

        // Step 1: validate the mode before touching the database. Failing fast
        // gives the settings UI a precise error without burning a round-trip.
        if (!AllowedModes.Contains(request.PermissionMode))
        {
            return Result.Failure<ProjectAgentPermissionsDto>(
                "invalid_permission_mode");
        }

        // Step 2: enforce the bypass-needs-skip pairing. The SDK silently
        // refuses to honour bypassPermissions without
        // allowDangerouslySkipPermissions=true; surfacing a validation error
        // here gives the UI a clear message instead of a no-op turn.
        if (request.PermissionMode == BypassMode && !request.AllowDangerouslySkipPermissions)
        {
            return Result.Failure<ProjectAgentPermissionsDto>(
                "bypass_requires_allow_dangerously_skip_permissions");
        }

        // Step 3: workspace-membership gate. Same shape as GetProjectHandler —
        // missing project, soft-deleted project and non-member caller all
        // collapse to a not-found failure so the controller can return 404
        // without leaking existence.
        var project = await _db.Projects
            .Where(p => p.Id == request.ProjectId)
            .Select(p => new { p.Id, p.WorkspaceId })
            .SingleOrDefaultAsync(cancellationToken);

        if (project is null)
        {
            return Result.Failure<ProjectAgentPermissionsDto>(
                $"{NotFoundPrefix} project {request.ProjectId} not found");
        }

        var isMember = await _db.WorkspaceMemberships
            .AnyAsync(
                m => m.WorkspaceId == project.WorkspaceId && m.UserId == request.CallerUserId,
                cancellationToken);

        if (!isMember)
        {
            return Result.Failure<ProjectAgentPermissionsDto>(
                $"{NotFoundPrefix} project {request.ProjectId} not found");
        }

        if (request.PermissionMode == BypassMode && !request.CallerIsSuperAdmin)
        {
            var role = await _db.WorkspaceMemberships
                .AsNoTracking()
                .Where(m => m.WorkspaceId == project.WorkspaceId && m.UserId == request.CallerUserId)
                .Select(m => (WorkspaceRole?)m.Role)
                .FirstOrDefaultAsync(cancellationToken);

            if (role is null || !role.Value.IsAtLeast(WorkspaceRole.Admin))
            {
                return Result.Failure<ProjectAgentPermissionsDto>(
                    "admin_required_for_bypass_permissions");
            }
        }

        // Step 4: load-or-create.
        // there's at most one row, so SingleOrDefault is safe even under a
        // write race (the second writer would catch a 23505 — but we're inside
        // a single transaction here, so the realistic concurrent case is two
        // tabs PUT-ing simultaneously, which is fine: the second wins).
        var row = await _db.ProjectAgentPermissions
            .FirstOrDefaultAsync(x => x.ProjectId == request.ProjectId, cancellationToken);

        if (row is null)
        {
            row = new ProjectAgentPermissions
            {
                ProjectId = request.ProjectId,
            };
            _db.ProjectAgentPermissions.Add(row);
        }

        row.PermissionMode = request.PermissionMode;
        row.AllowDangerouslySkipPermissions = request.AllowDangerouslySkipPermissions;
        row.AllowedTools = request.AllowedTools.ToList();
        row.DisallowedTools = request.DisallowedTools.ToList();
        row.AdditionalDirectories = request.AdditionalDirectories.ToList();

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "UpsertProjectAgentPermissions: project {ProjectId} updated by {UserId}. " +
            "mode={Mode} skip={Skip} allowed={AllowedCount} disallowed={DisallowedCount} dirs={DirCount}.",
            request.ProjectId,
            request.CallerUserId,
            row.PermissionMode,
            row.AllowDangerouslySkipPermissions,
            row.AllowedTools.Count,
            row.DisallowedTools.Count,
            row.AdditionalDirectories.Count);

        return Result.Success(new ProjectAgentPermissionsDto(
            ProjectId: row.ProjectId,
            PermissionMode: row.PermissionMode,
            AllowDangerouslySkipPermissions: row.AllowDangerouslySkipPermissions,
            AllowedTools: row.AllowedTools.AsReadOnly(),
            DisallowedTools: row.DisallowedTools.AsReadOnly(),
            AdditionalDirectories: row.AdditionalDirectories.AsReadOnly()));
    }
}

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Source.Features.EnvironmentBackup.Models;
using Source.Features.GitHub.Models;
using Source.Features.Projects.Models;
using Source.Features.Projects.Services;
using Source.Features.ProjectKanban.Models;
using Source.Features.ProjectSecrets.Models;
using Source.Features.ProjectSecrets.Services;
using Source.Features.Specifications.Models;
using Source.Features.SystemSettings.Services;
using Source.Features.Users.Models;
using Source.Features.Workspaces.Models;
using Source.Features.WorkspaceSpecs.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.EnvironmentBackup.Commands.ImportEnvironment;

/// <summary>
/// Restores an <see cref="EnvironmentSnapshotDto"/> in FK-safe order inside one
/// transaction. Secrets are re-encrypted under the target environment's keys:
/// SystemSettings via <see cref="ISystemSettingsService.SetAsync"/>; ProjectSecret +
/// Project cursor key via <see cref="SecretEncryptionService"/> (which lazily creates the
/// per-project DEK under the target master key). Upserts are keyed on stable Id (Slug for
/// specs) so re-running is safe.
/// </summary>
public sealed class ImportEnvironmentHandler
    : ICommandHandler<ImportEnvironmentCommand, Result<EnvironmentImportSummary>>
{
    private readonly ApplicationDbContext _db;
    private readonly ISystemSettingsService _settings;
    private readonly SecretEncryptionService _encryption;
    private readonly UserManager<User> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly ILogger<ImportEnvironmentHandler> _logger;

    public ImportEnvironmentHandler(
        ApplicationDbContext db,
        ISystemSettingsService settings,
        SecretEncryptionService encryption,
        UserManager<User> userManager,
        RoleManager<IdentityRole> roleManager,
        ILogger<ImportEnvironmentHandler> logger)
    {
        _db = db;
        _settings = settings;
        _encryption = encryption;
        _userManager = userManager;
        _roleManager = roleManager;
        _logger = logger;
    }

    public async Task<Result<EnvironmentImportSummary>> Handle(
        ImportEnvironmentCommand request,
        CancellationToken ct)
    {
        var snapshot = request.Snapshot;
        if (snapshot is null)
        {
            return Result.Failure<EnvironmentImportSummary>("snapshot_required");
        }

        if (!EnvironmentSnapshotVersions.IsSupported(snapshot.Version))
        {
            return Result.Failure<EnvironmentImportSummary>(
                $"unsupported_version: '{snapshot.Version}'. Supported: {string.Join(", ", EnvironmentSnapshotVersions.Supported)}.");
        }

        var summary = new EnvironmentImportSummary { Version = snapshot.Version };
        IReadOnlyDictionary<string, string> userIdMap = new Dictionary<string, string>();
        HashSet<string> existingUserIds = new(StringComparer.Ordinal);

        // SystemSettings go through SetAsync, which does its own SaveChanges +
        // cache invalidation per key. They have no FK into the rest of the graph,
        // so we apply them first (and outside the EF transaction's entity tracking
        // concerns). The crypto master key, if present, lands here so subsequent
        // project-secret re-encryption is self-consistent.
        await ImportSystemSettingsAsync(snapshot, summary, ct);

        // Npgsql's retrying execution strategy (EnableRetryOnFailure) forbids
        // user-initiated transactions outside CreateExecutionStrategy().ExecuteAsync.
        var strategy = _db.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                try
                {
                    (userIdMap, existingUserIds) = await ImportUsersAsync(snapshot, summary, ct);
                    var workspaceIdMap = await ImportWorkspacesAsync(snapshot, userIdMap, existingUserIds, summary, ct);
                    await ImportMembershipsAsync(snapshot, workspaceIdMap, userIdMap, existingUserIds, summary, ct);
                    await ImportInvitesAsync(snapshot, workspaceIdMap, userIdMap, existingUserIds, summary, ct);
                    await ImportWorkspaceSpecsAsync(snapshot, workspaceIdMap, userIdMap, existingUserIds, summary, ct);
                    await ImportGithubInstallationsAsync(snapshot, workspaceIdMap, summary, ct);
                    var importedProjectIds = await ImportProjectsAsync(snapshot, workspaceIdMap, userIdMap, existingUserIds, summary, ct);
                    await ImportProjectBranchesAsync(snapshot, importedProjectIds, summary, ct);
                    await ImportProjectSecretsAsync(snapshot, importedProjectIds, userIdMap, existingUserIds, summary, ct);
                    await ImportProjectAgentPermissionsAsync(snapshot, importedProjectIds, summary, ct);
                    await ImportSpecificationsAsync(snapshot, importedProjectIds, userIdMap, existingUserIds, summary, ct);
                    await ImportKanbanCardsAsync(snapshot, importedProjectIds, userIdMap, existingUserIds, summary, ct);

                    await _db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                }
                catch
                {
                    await tx.RollbackAsync(ct);
                    throw;
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ImportEnvironment: restore failed and was rolled back.");
            return Result.Failure<EnvironmentImportSummary>($"import_failed: {DescribeException(ex)}");
        }

        _logger.LogInformation(
            "ImportEnvironment: restored v{Version} — {Users} users, {Workspaces} workspaces, {Projects} projects, {Cards} cards.",
            summary.Version, summary.Users, summary.Workspaces, summary.Projects, summary.KanbanCards);

        return Result.Success(summary);
    }

    // ---------------- SystemSettings ----------------

    private async Task ImportSystemSettingsAsync(
        EnvironmentSnapshotDto snapshot, EnvironmentImportSummary summary, CancellationToken ct)
    {
        foreach (var s in snapshot.SystemSettings)
        {
            if (string.IsNullOrWhiteSpace(s.Key)) continue;
            // SetAsync re-encrypts when isSecret is true, using the source row's own
            // IsSecret flag (authoritative for non-catalog rows like the crypto master key).
            await _settings.SetAsync(s.Key, s.Value, s.IsSecret, updatedBy: "system:environment-import", ct);
            summary.SystemSettings++;
        }
    }

    // ---------------- Users ----------------

    private async Task<(IReadOnlyDictionary<string, string> IdMap, HashSet<string> ExistingIds)> ImportUsersAsync(
        EnvironmentSnapshotDto snapshot, EnvironmentImportSummary summary, CancellationToken ct)
    {
        var userIdMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var existingUserIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var u in snapshot.Users)
        {
            if (string.IsNullOrWhiteSpace(u.Id)) continue;

            // Match by stable Id first, fall back to email — relate to the EXISTING
            // identity store rather than recreating the identity schema.
            var existing = await _db.Users.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == u.Id, ct);

            if (existing is null && !string.IsNullOrWhiteSpace(u.Email))
            {
                existing = await _db.Users.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Email == u.Email, ct);
            }

            if (existing is null)
            {
                existing = new User { Id = u.Id };
                _db.Users.Add(existing);
            }

            userIdMap[u.Id] = existing.Id;
            existingUserIds.Add(existing.Id);

            existing.Email = u.Email;
            existing.NormalizedEmail = u.Email?.ToUpperInvariant();
            existing.UserName = u.UserName ?? u.Email;
            existing.NormalizedUserName = (u.UserName ?? u.Email)?.ToUpperInvariant();
            existing.FirstName = u.FirstName;
            existing.LastName = u.LastName;
            existing.EmailConfirmed = u.EmailConfirmed;
            existing.IsOnboarded = u.IsOnboarded;
            existing.Credits = u.Credits;

            if (!string.IsNullOrWhiteSpace(u.PasswordHash))
            {
                existing.PasswordHash = u.PasswordHash;
            }
            else
            {
                summary.UsersWithoutPasswordHash++;
            }

            if (string.IsNullOrWhiteSpace(existing.SecurityStamp))
            {
                existing.SecurityStamp = Guid.NewGuid().ToString();
            }
            if (string.IsNullOrWhiteSpace(existing.ConcurrencyStamp))
            {
                existing.ConcurrencyStamp = Guid.NewGuid().ToString();
            }

            summary.Users++;
        }

        // Persist user rows before touching role join tables.
        await _db.SaveChangesAsync(ct);

        // Roles: re-apply via the framework so the join rows resolve correctly.
        foreach (var u in snapshot.Users)
        {
            if (u.Roles.Count == 0) continue;
            if (!userIdMap.TryGetValue(u.Id, out var resolvedId)) continue;

            var user = await _userManager.FindByIdAsync(resolvedId);
            if (user is null) continue;

            var current = await _userManager.GetRolesAsync(user);
            foreach (var role in u.Roles)
            {
                if (string.IsNullOrWhiteSpace(role)) continue;
                if (current.Contains(role)) continue;
                if (!await _roleManager.RoleExistsAsync(role)) continue;
                await _userManager.AddToRoleAsync(user, role);
            }
        }

        return (userIdMap, existingUserIds);
    }

    // ---------------- Workspaces ----------------

    private async Task<IReadOnlyDictionary<Guid, Guid>> ImportWorkspacesAsync(
        EnvironmentSnapshotDto snapshot,
        IReadOnlyDictionary<string, string> userIdMap,
        HashSet<string> existingUserIds,
        EnvironmentImportSummary summary,
        CancellationToken ct)
    {
        var workspaceIdMap = new Dictionary<Guid, Guid>();

        foreach (var w in snapshot.Workspaces)
        {
            var ownerId = await ResolveRequiredUserReferenceAsync(
                w.OwnerId, userIdMap, existingUserIds, $"workspace {w.Id} owner", ct);
            if (ownerId is null) continue;

            // Match by stable Id first, fall back to slug — relate to the EXISTING
            // workspace row rather than colliding on IX_Workspaces_Slug.
            var existing = await _db.Workspaces.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == w.Id, ct);

            if (existing is null && !string.IsNullOrWhiteSpace(w.Slug))
            {
                existing = await _db.Workspaces.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Slug == w.Slug, ct);
            }

            if (existing is null)
            {
                existing = new Workspace { Id = w.Id };
                _db.Workspaces.Add(existing);
            }

            workspaceIdMap[w.Id] = existing.Id;

            existing.Slug = w.Slug;
            existing.Name = w.Name;
            existing.OwnerId = ownerId;
            existing.AllowProjectCursorApiKeyOverride = w.AllowProjectCursorApiKeyOverride;
            existing.IsDeleted = false;

            if (string.IsNullOrWhiteSpace(w.CursorApiKey))
            {
                existing.EncryptedCursorApiKey = null;
            }
            else
            {
                var (ciphertext, nonce, dekVersion) =
                    await _encryption.EncryptForWorkspaceAsync(existing.Id, w.CursorApiKey, ct);
                existing.EncryptedCursorApiKey = ProjectByokEnvelope.Pack(ciphertext, nonce, dekVersion);
            }

            summary.Workspaces++;
        }

        return workspaceIdMap;
    }

    private async Task ImportMembershipsAsync(
        EnvironmentSnapshotDto snapshot,
        IReadOnlyDictionary<Guid, Guid> workspaceIdMap,
        IReadOnlyDictionary<string, string> userIdMap,
        HashSet<string> existingUserIds,
        EnvironmentImportSummary summary,
        CancellationToken ct)
    {
        foreach (var m in snapshot.WorkspaceMemberships)
        {
            var workspaceId = MapWorkspaceId(m.WorkspaceId, workspaceIdMap);
            if (workspaceId is null)
            {
                _logger.LogWarning(
                    "ImportEnvironment: skipping membership {MembershipId} — workspace {WorkspaceId} not imported.",
                    m.Id, m.WorkspaceId);
                continue;
            }

            var userId = await ResolveRequiredUserReferenceAsync(
                m.UserId, userIdMap, existingUserIds, $"membership {m.Id} user", ct);
            if (userId is null) continue;

            // Match by stable Id first, fall back to (WorkspaceId, UserId) — after
            // workspace/user remapping the snapshot row may map onto an existing pair.
            var existing = await _db.WorkspaceMemberships
                .FirstOrDefaultAsync(x => x.Id == m.Id, ct);

            if (existing is null)
            {
                existing = await _db.WorkspaceMemberships
                    .FirstOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.UserId == userId, ct);
            }

            if (existing is null)
            {
                existing = new WorkspaceMembership { Id = m.Id };
                _db.WorkspaceMemberships.Add(existing);
            }

            existing.WorkspaceId = workspaceId.Value;
            existing.UserId = userId;
            existing.Role = (WorkspaceRole)m.Role;
            summary.WorkspaceMemberships++;
        }
    }

    private async Task ImportInvitesAsync(
        EnvironmentSnapshotDto snapshot,
        IReadOnlyDictionary<Guid, Guid> workspaceIdMap,
        IReadOnlyDictionary<string, string> userIdMap,
        HashSet<string> existingUserIds,
        EnvironmentImportSummary summary,
        CancellationToken ct)
    {
        foreach (var i in snapshot.WorkspaceInvites)
        {
            var workspaceId = MapWorkspaceId(i.WorkspaceId, workspaceIdMap);
            if (workspaceId is null)
            {
                _logger.LogWarning(
                    "ImportEnvironment: skipping invite {InviteId} — workspace {WorkspaceId} not imported.",
                    i.Id, i.WorkspaceId);
                continue;
            }

            var invitedById = await ResolveRequiredUserReferenceAsync(
                i.InvitedById, userIdMap, existingUserIds, $"invite {i.Id} inviter", ct);
            if (invitedById is null) continue;

            var existing = await _db.WorkspaceInvites
                .FirstOrDefaultAsync(x => x.Id == i.Id, ct);

            if (existing is null)
            {
                existing = new WorkspaceInvite { Id = i.Id };
                _db.WorkspaceInvites.Add(existing);
            }

            existing.WorkspaceId = workspaceId.Value;
            existing.Email = i.Email;
            existing.Role = (WorkspaceRole)i.Role;
            existing.InvitedById = invitedById;
            existing.Token = i.Token;
            existing.ExpiresAt = i.ExpiresAt;
            existing.AcceptedAt = i.AcceptedAt;
            existing.AcceptedByUserId = await ResolveOptionalUserReferenceAsync(
                i.AcceptedByUserId, userIdMap, existingUserIds, ct);
            summary.WorkspaceInvites++;
        }
    }

    private async Task ImportWorkspaceSpecsAsync(
        EnvironmentSnapshotDto snapshot,
        IReadOnlyDictionary<Guid, Guid> workspaceIdMap,
        IReadOnlyDictionary<string, string> userIdMap,
        HashSet<string> existingUserIds,
        EnvironmentImportSummary summary,
        CancellationToken ct)
    {
        foreach (var s in snapshot.WorkspaceSpecs)
        {
            var workspaceId = MapWorkspaceId(s.WorkspaceId, workspaceIdMap);
            if (workspaceId is null)
            {
                _logger.LogWarning(
                    "ImportEnvironment: skipping workspace spec {SpecId} — workspace {WorkspaceId} not imported.",
                    s.Id, s.WorkspaceId);
                continue;
            }

            var createdByUserId = await ResolveRequiredUserReferenceAsync(
                s.CreatedByUserId, userIdMap, existingUserIds, $"workspace spec {s.Id} creator", ct);
            if (createdByUserId is null) continue;

            var updatedByUserId = await ResolveRequiredUserReferenceAsync(
                s.UpdatedByUserId, userIdMap, existingUserIds, $"workspace spec {s.Id} updater", ct)
                ?? createdByUserId;

            var existing = await _db.WorkspaceSpecs
                .FirstOrDefaultAsync(x => x.Id == s.Id, ct);

            if (existing is null)
            {
                existing = new WorkspaceSpec { Id = s.Id };
                _db.WorkspaceSpecs.Add(existing);
            }

            existing.WorkspaceId = workspaceId.Value;
            existing.Name = s.Name;
            existing.Description = s.Description;
            existing.Content = s.Content;
            existing.CreatedByUserId = createdByUserId;
            existing.UpdatedByUserId = updatedByUserId;
            summary.WorkspaceSpecs++;
        }
    }

    // ---------------- GitHub installations ----------------

    private async Task ImportGithubInstallationsAsync(
        EnvironmentSnapshotDto snapshot,
        IReadOnlyDictionary<Guid, Guid> workspaceIdMap,
        EnvironmentImportSummary summary,
        CancellationToken ct)
    {
        foreach (var g in snapshot.GithubInstallations)
        {
            var workspaceId = MapWorkspaceId(g.WorkspaceId, workspaceIdMap);
            if (workspaceId is null)
            {
                _logger.LogWarning(
                    "ImportEnvironment: skipping GitHub installation {InstallationId} — workspace {WorkspaceId} not imported.",
                    g.Id, g.WorkspaceId);
                continue;
            }

            var existing = await _db.GithubInstallations
                .FirstOrDefaultAsync(x => x.Id == g.Id, ct);

            if (existing is null)
            {
                existing = new GithubInstallation { Id = g.Id };
                _db.GithubInstallations.Add(existing);
            }

            existing.WorkspaceId = workspaceId.Value;
            existing.InstallationId = g.InstallationId;
            existing.AccountLogin = g.AccountLogin;
            existing.AccountType = g.AccountType;
            existing.AccountAvatarUrl = g.AccountAvatarUrl;
            existing.Suspended = g.Suspended;
            existing.UserAccessToken = g.UserAccessToken;
            existing.UserAccessTokenExpiresAt = g.UserAccessTokenExpiresAt;
            existing.UserRefreshToken = g.UserRefreshToken;
            existing.UserRefreshTokenExpiresAt = g.UserRefreshTokenExpiresAt;
            existing.UserLogin = g.UserLogin;
            summary.GithubInstallations++;
        }
    }

    // ---------------- Projects ----------------

    private async Task<HashSet<Guid>> ImportProjectsAsync(
        EnvironmentSnapshotDto snapshot,
        IReadOnlyDictionary<Guid, Guid> workspaceIdMap,
        IReadOnlyDictionary<string, string> userIdMap,
        HashSet<string> existingUserIds,
        EnvironmentImportSummary summary,
        CancellationToken ct)
    {
        var importedProjectIds = new HashSet<Guid>();

        foreach (var p in snapshot.Projects)
        {
            var workspaceId = MapWorkspaceId(p.WorkspaceId, workspaceIdMap);
            if (workspaceId is null)
            {
                _logger.LogWarning(
                    "ImportEnvironment: skipping project {ProjectId} — workspace {WorkspaceId} not imported.",
                    p.Id, p.WorkspaceId);
                continue;
            }

            var ownerUserId = await ResolveRequiredUserReferenceAsync(
                p.OwnerUserId, userIdMap, existingUserIds, $"project {p.Id} owner", ct);
            if (ownerUserId is null) continue;

            var existing = await _db.Projects.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == p.Id, ct);

            if (existing is null)
            {
                existing = new Project { Id = p.Id };
                _db.Projects.Add(existing);
            }

            existing.WorkspaceId = workspaceId.Value;
            existing.OwnerUserId = ownerUserId;
            existing.Name = p.Name;
            existing.GithubRepoOwner = p.GithubRepoOwner;
            existing.GithubRepoName = p.GithubRepoName;
            existing.PreviewPort = p.PreviewPort;
            existing.RuntimeCpuKind = p.RuntimeCpuKind;
            existing.RuntimeCpus = p.RuntimeCpus;
            existing.RuntimeMemoryMb = p.RuntimeMemoryMb;
            existing.RuntimeVolumeSizeGb = p.RuntimeVolumeSizeGb;
            existing.Spec = p.Spec;
            existing.SpecVersion = p.SpecVersion;
            existing.IsDeleted = false;

            if (p.GithubInstallationId is { } githubInstallationId
                && !await _db.GithubInstallations.AnyAsync(g => g.Id == githubInstallationId, ct))
            {
                _logger.LogWarning(
                    "ImportEnvironment: GithubInstallation {InstallationId} missing; clearing on project {ProjectId}.",
                    githubInstallationId, p.Id);
                existing.GithubInstallationId = null;
            }
            else
            {
                existing.GithubInstallationId = p.GithubInstallationId;
            }

            if (p.TemplateId is { } templateId
                && !await _db.ProjectTemplates.IgnoreQueryFilters().AnyAsync(t => t.Id == templateId, ct))
            {
                _logger.LogWarning(
                    "ImportEnvironment: ProjectTemplate {TemplateId} missing; clearing on project {ProjectId}.",
                    templateId, p.Id);
                existing.TemplateId = null;
            }
            else
            {
                existing.TemplateId = p.TemplateId;
            }

            var modelId = p.ModelId;
            if (modelId is not null && !await _db.CursorModels.AnyAsync(m => m.Id == modelId, ct))
            {
                _logger.LogWarning(
                    "ImportEnvironment: CursorModel {ModelId} missing; clearing on project {ProjectId}.",
                    modelId, p.Id);
                modelId = null;
            }

            // ModelId has a private setter — go through the rich method.
            existing.SetModel(modelId);

            // Re-encrypt the cursor key under THIS environment's project DEK.
            if (string.IsNullOrWhiteSpace(p.CursorApiKey))
            {
                existing.EncryptedCursorApiKey = null;
            }
            else
            {
                var (ciphertext, nonce, dekVersion) =
                    await _encryption.EncryptAsync(p.Id, p.CursorApiKey, ct);
                existing.EncryptedCursorApiKey = ProjectByokEnvelope.Pack(ciphertext, nonce, dekVersion);
            }

            summary.Projects++;
            importedProjectIds.Add(p.Id);
        }

        return importedProjectIds;
    }

    private async Task ImportProjectBranchesAsync(
        EnvironmentSnapshotDto snapshot, HashSet<Guid> projectIds, EnvironmentImportSummary summary, CancellationToken ct)
    {
        foreach (var b in snapshot.ProjectBranches)
        {
            if (!projectIds.Contains(b.ProjectId))
            {
                _logger.LogWarning(
                    "ImportEnvironment: skipping branch {BranchId} — parent project {ProjectId} not in snapshot.",
                    b.Id, b.ProjectId);
                continue;
            }

            var existing = await _db.ProjectBranches
                .FirstOrDefaultAsync(x => x.Id == b.Id, ct);

            if (existing is null)
            {
                existing = new ProjectBranch { Id = b.Id };
                _db.ProjectBranches.Add(existing);
            }

            existing.ProjectId = b.ProjectId;
            existing.Name = b.Name;
            existing.IsDefault = b.IsDefault;
            // IsArchived / ArchivedAt have private setters; set via EF property bag.
            var entry = _db.Entry(existing);
            entry.Property(nameof(ProjectBranch.IsArchived)).CurrentValue = b.IsArchived;
            entry.Property(nameof(ProjectBranch.ArchivedAt)).CurrentValue = b.ArchivedAt;
            summary.ProjectBranches++;
        }
    }

    // ---------------- Project secrets ----------------

    private async Task ImportProjectSecretsAsync(
        EnvironmentSnapshotDto snapshot,
        HashSet<Guid> projectIds,
        IReadOnlyDictionary<string, string> userIdMap,
        HashSet<string> existingUserIds,
        EnvironmentImportSummary summary,
        CancellationToken ct)
    {
        foreach (var s in snapshot.ProjectSecrets)
        {
            if (!projectIds.Contains(s.ProjectId))
            {
                _logger.LogWarning(
                    "ImportEnvironment: skipping secret {SecretId} — parent project {ProjectId} not in snapshot.",
                    s.Id, s.ProjectId);
                continue;
            }

            var (ciphertext, nonce, dekVersion) =
                await _encryption.EncryptAsync(s.ProjectId, s.Value, ct);

            var existing = await _db.ProjectSecrets.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == s.Id, ct);

            if (existing is null)
            {
                existing = new ProjectSecret { Id = s.Id };
                _db.ProjectSecrets.Add(existing);
            }

            existing.ProjectId = s.ProjectId;
            existing.Key = s.Key;
            existing.Ciphertext = ciphertext;
            existing.Nonce = nonce;
            existing.DekVersion = dekVersion;
            existing.Version = s.Version;
            existing.CreatedBy = await ResolveOptionalUserReferenceAsync(
                s.CreatedBy, userIdMap, existingUserIds, ct);
            existing.IsDeleted = false;
            summary.ProjectSecrets++;
        }
    }

    private async Task ImportProjectAgentPermissionsAsync(
        EnvironmentSnapshotDto snapshot, HashSet<Guid> projectIds, EnvironmentImportSummary summary, CancellationToken ct)
    {
        foreach (var a in snapshot.ProjectAgentPermissions)
        {
            if (!projectIds.Contains(a.ProjectId))
            {
                _logger.LogWarning(
                    "ImportEnvironment: skipping agent permissions {PermissionsId} — parent project {ProjectId} not in snapshot.",
                    a.Id, a.ProjectId);
                continue;
            }

            var existing = await _db.ProjectAgentPermissions
                .FirstOrDefaultAsync(x => x.Id == a.Id, ct);

            if (existing is null)
            {
                existing = new ProjectAgentPermissions { Id = a.Id };
                _db.ProjectAgentPermissions.Add(existing);
            }

            existing.ProjectId = a.ProjectId;
            existing.PermissionMode = a.PermissionMode;
            existing.AllowDangerouslySkipPermissions = a.AllowDangerouslySkipPermissions;
            existing.AllowedTools = a.AllowedTools.ToList();
            existing.DisallowedTools = a.DisallowedTools.ToList();
            existing.AdditionalDirectories = a.AdditionalDirectories.ToList();
            summary.ProjectAgentPermissions++;
        }
    }

    // ---------------- Specifications ----------------

    private async Task ImportSpecificationsAsync(
        EnvironmentSnapshotDto snapshot,
        HashSet<Guid> projectIds,
        IReadOnlyDictionary<string, string> userIdMap,
        HashSet<string> existingUserIds,
        EnvironmentImportSummary summary,
        CancellationToken ct)
    {
        foreach (var s in snapshot.Specifications)
        {
            if (!projectIds.Contains(s.ProjectId))
            {
                _logger.LogWarning(
                    "ImportEnvironment: skipping specification {SpecificationId} — parent project {ProjectId} not in snapshot.",
                    s.Id, s.ProjectId);
                continue;
            }

            // Match on Id, fall back to (ProjectId, Slug) since the slug is the stable
            // re-creation key in this slice.
            var existing = await _db.Specifications.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == s.Id, ct)
                ?? await _db.Specifications.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.ProjectId == s.ProjectId && x.Slug == s.Slug, ct);

            if (existing is null)
            {
                existing = (Specification)Activator.CreateInstance(typeof(Specification), nonPublic: true)!;
                _db.Specifications.Add(existing);
            }

            var entry = _db.Entry(existing);
            entry.Property(nameof(Specification.Id)).CurrentValue = s.Id;
            entry.Property(nameof(Specification.ProjectId)).CurrentValue = s.ProjectId;
            entry.Property(nameof(Specification.Slug)).CurrentValue = s.Slug;
            entry.Property(nameof(Specification.Name)).CurrentValue = s.Name;
            entry.Property(nameof(Specification.Content)).CurrentValue = s.Content;
            entry.Property(nameof(Specification.Status)).CurrentValue = (SpecificationStatus)s.Status;
            entry.Property(nameof(Specification.CreatedBy)).CurrentValue =
                await ResolveOptionalUserReferenceAsync(s.CreatedBy, userIdMap, existingUserIds, ct);
            existing.IsDeleted = false;
            summary.Specifications++;
        }
    }

    // ---------------- Kanban cards (+ subtasks) ----------------

    private async Task ImportKanbanCardsAsync(
        EnvironmentSnapshotDto snapshot,
        HashSet<Guid> projectIds,
        IReadOnlyDictionary<string, string> userIdMap,
        HashSet<string> existingUserIds,
        EnvironmentImportSummary summary,
        CancellationToken ct)
    {
        foreach (var c in snapshot.KanbanCards)
        {
            if (!projectIds.Contains(c.ProjectId))
            {
                _logger.LogWarning(
                    "ImportEnvironment: skipping kanban card {CardId} — parent project {ProjectId} not in snapshot.",
                    c.Id, c.ProjectId);
                continue;
            }

            var existing = await _db.ProjectKanbanCards.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == c.Id, ct);

            if (existing is null)
            {
                existing = (ProjectKanbanCard)Activator.CreateInstance(typeof(ProjectKanbanCard), nonPublic: true)!;
                _db.ProjectKanbanCards.Add(existing);
            }

            var entry = _db.Entry(existing);
            entry.Property(nameof(ProjectKanbanCard.Id)).CurrentValue = c.Id;
            entry.Property(nameof(ProjectKanbanCard.ProjectId)).CurrentValue = c.ProjectId;
            entry.Property(nameof(ProjectKanbanCard.Title)).CurrentValue = c.Title;
            entry.Property(nameof(ProjectKanbanCard.Description)).CurrentValue = c.Description;
            entry.Property(nameof(ProjectKanbanCard.Status)).CurrentValue = (ProjectKanbanCardStatus)c.Status;
            entry.Property(nameof(ProjectKanbanCard.Position)).CurrentValue = c.Position;
            entry.Property(nameof(ProjectKanbanCard.Priority)).CurrentValue = (ProjectKanbanCardPriority)c.Priority;
            entry.Property(nameof(ProjectKanbanCard.DueDate)).CurrentValue = c.DueDate;
            entry.Property(nameof(ProjectKanbanCard.CreatedBy)).CurrentValue =
                await ResolveOptionalUserReferenceAsync(c.CreatedBy, userIdMap, existingUserIds, ct);
            entry.Property(nameof(ProjectKanbanCard.Source)).CurrentValue = (ProjectKanbanCardSource)c.Source;
            entry.Property(nameof(ProjectKanbanCard.CreatedOnBranch)).CurrentValue = c.CreatedOnBranch;
            existing.IsDeleted = false;
            summary.KanbanCards++;

            foreach (var t in c.Subtasks)
            {
                var existingSubtask = await _db.ProjectKanbanCardSubtasks.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Id == t.Id, ct);

                if (existingSubtask is null)
                {
                    existingSubtask = (ProjectKanbanCardSubtask)Activator.CreateInstance(
                        typeof(ProjectKanbanCardSubtask), nonPublic: true)!;
                    _db.ProjectKanbanCardSubtasks.Add(existingSubtask);
                }

                var subEntry = _db.Entry(existingSubtask);
                subEntry.Property(nameof(ProjectKanbanCardSubtask.Id)).CurrentValue = t.Id;
                subEntry.Property(nameof(ProjectKanbanCardSubtask.ProjectKanbanCardId)).CurrentValue = c.Id;
                subEntry.Property(nameof(ProjectKanbanCardSubtask.Title)).CurrentValue = t.Title;
                subEntry.Property(nameof(ProjectKanbanCardSubtask.IsCompleted)).CurrentValue = t.IsCompleted;
                subEntry.Property(nameof(ProjectKanbanCardSubtask.Position)).CurrentValue = t.Position;
                existingSubtask.IsDeleted = false;
                summary.KanbanSubtasks++;
            }
        }
    }

    private static Guid? MapWorkspaceId(
        Guid snapshotWorkspaceId,
        IReadOnlyDictionary<Guid, Guid> workspaceIdMap) =>
        workspaceIdMap.TryGetValue(snapshotWorkspaceId, out var mapped) ? mapped : null;

    private async Task<string?> ResolveOptionalUserReferenceAsync(
        string? userId,
        IReadOnlyDictionary<string, string> userIdMap,
        HashSet<string> existingUserIds,
        CancellationToken ct)
    {
        var resolved = await ResolveUserReferenceCoreAsync(userId, userIdMap, existingUserIds, ct);
        if (userId is not null && resolved is null)
        {
            _logger.LogWarning(
                "ImportEnvironment: user {UserId} not found; clearing optional reference.",
                userId);
        }

        return resolved;
    }

    private async Task<string?> ResolveRequiredUserReferenceAsync(
        string? userId,
        IReadOnlyDictionary<string, string> userIdMap,
        HashSet<string> existingUserIds,
        string context,
        CancellationToken ct)
    {
        var resolved = await ResolveUserReferenceCoreAsync(userId, userIdMap, existingUserIds, ct);
        if (userId is not null && resolved is null)
        {
            _logger.LogWarning(
                "ImportEnvironment: skipping {Context} — user {UserId} not found.",
                context, userId);
        }

        return resolved;
    }

    private async Task<string?> ResolveUserReferenceCoreAsync(
        string? userId,
        IReadOnlyDictionary<string, string> userIdMap,
        HashSet<string> existingUserIds,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId)) return null;

        var mapped = userIdMap.TryGetValue(userId, out var remapped) ? remapped : userId;
        if (existingUserIds.Contains(mapped)) return mapped;

        if (await _db.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == mapped, ct))
        {
            existingUserIds.Add(mapped);
            return mapped;
        }

        return null;
    }

    private static string DescribeException(Exception ex)
    {
        var detail = ex;
        while (detail.InnerException is not null)
        {
            detail = detail.InnerException;
        }

        return detail.Message;
    }
}

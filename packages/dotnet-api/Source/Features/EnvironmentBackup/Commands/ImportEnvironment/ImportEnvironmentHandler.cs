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
                    await ImportUsersAsync(snapshot, summary, ct);
                    await ImportWorkspacesAsync(snapshot, summary, ct);
                    await ImportMembershipsAsync(snapshot, summary, ct);
                    await ImportInvitesAsync(snapshot, summary, ct);
                    await ImportWorkspaceSpecsAsync(snapshot, summary, ct);
                    await ImportGithubInstallationsAsync(snapshot, summary, ct);
                    await ImportProjectsAsync(snapshot, summary, ct);
                    await ImportProjectBranchesAsync(snapshot, summary, ct);
                    await ImportProjectSecretsAsync(snapshot, summary, ct);
                    await ImportProjectAgentPermissionsAsync(snapshot, summary, ct);
                    await ImportSpecificationsAsync(snapshot, summary, ct);
                    await ImportKanbanCardsAsync(snapshot, summary, ct);

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
            return Result.Failure<EnvironmentImportSummary>($"import_failed: {ex.Message}");
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

    private async Task ImportUsersAsync(
        EnvironmentSnapshotDto snapshot, EnvironmentImportSummary summary, CancellationToken ct)
    {
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
            var user = await _userManager.FindByIdAsync(u.Id);
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
    }

    // ---------------- Workspaces ----------------

    private async Task ImportWorkspacesAsync(
        EnvironmentSnapshotDto snapshot, EnvironmentImportSummary summary, CancellationToken ct)
    {
        foreach (var w in snapshot.Workspaces)
        {
            var existing = await _db.Workspaces.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == w.Id, ct);

            if (existing is null)
            {
                existing = new Workspace { Id = w.Id };
                _db.Workspaces.Add(existing);
            }

            existing.Slug = w.Slug;
            existing.Name = w.Name;
            existing.OwnerId = w.OwnerId;
            existing.IsDeleted = false;
            summary.Workspaces++;
        }
    }

    private async Task ImportMembershipsAsync(
        EnvironmentSnapshotDto snapshot, EnvironmentImportSummary summary, CancellationToken ct)
    {
        foreach (var m in snapshot.WorkspaceMemberships)
        {
            var existing = await _db.WorkspaceMemberships
                .FirstOrDefaultAsync(x => x.Id == m.Id, ct);

            if (existing is null)
            {
                existing = new WorkspaceMembership { Id = m.Id };
                _db.WorkspaceMemberships.Add(existing);
            }

            existing.WorkspaceId = m.WorkspaceId;
            existing.UserId = m.UserId;
            existing.Role = (WorkspaceRole)m.Role;
            summary.WorkspaceMemberships++;
        }
    }

    private async Task ImportInvitesAsync(
        EnvironmentSnapshotDto snapshot, EnvironmentImportSummary summary, CancellationToken ct)
    {
        foreach (var i in snapshot.WorkspaceInvites)
        {
            var existing = await _db.WorkspaceInvites
                .FirstOrDefaultAsync(x => x.Id == i.Id, ct);

            if (existing is null)
            {
                existing = new WorkspaceInvite { Id = i.Id };
                _db.WorkspaceInvites.Add(existing);
            }

            existing.WorkspaceId = i.WorkspaceId;
            existing.Email = i.Email;
            existing.Role = (WorkspaceRole)i.Role;
            existing.InvitedById = i.InvitedById;
            existing.Token = i.Token;
            existing.ExpiresAt = i.ExpiresAt;
            existing.AcceptedAt = i.AcceptedAt;
            existing.AcceptedByUserId = i.AcceptedByUserId;
            summary.WorkspaceInvites++;
        }
    }

    private async Task ImportWorkspaceSpecsAsync(
        EnvironmentSnapshotDto snapshot, EnvironmentImportSummary summary, CancellationToken ct)
    {
        foreach (var s in snapshot.WorkspaceSpecs)
        {
            var existing = await _db.WorkspaceSpecs
                .FirstOrDefaultAsync(x => x.Id == s.Id, ct);

            if (existing is null)
            {
                existing = new WorkspaceSpec { Id = s.Id };
                _db.WorkspaceSpecs.Add(existing);
            }

            existing.WorkspaceId = s.WorkspaceId;
            existing.Name = s.Name;
            existing.Description = s.Description;
            existing.Content = s.Content;
            existing.CreatedByUserId = s.CreatedByUserId;
            existing.UpdatedByUserId = s.UpdatedByUserId;
            summary.WorkspaceSpecs++;
        }
    }

    // ---------------- GitHub installations ----------------

    private async Task ImportGithubInstallationsAsync(
        EnvironmentSnapshotDto snapshot, EnvironmentImportSummary summary, CancellationToken ct)
    {
        foreach (var g in snapshot.GithubInstallations)
        {
            var existing = await _db.GithubInstallations
                .FirstOrDefaultAsync(x => x.Id == g.Id, ct);

            if (existing is null)
            {
                existing = new GithubInstallation { Id = g.Id };
                _db.GithubInstallations.Add(existing);
            }

            existing.WorkspaceId = g.WorkspaceId;
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

    private async Task ImportProjectsAsync(
        EnvironmentSnapshotDto snapshot, EnvironmentImportSummary summary, CancellationToken ct)
    {
        foreach (var p in snapshot.Projects)
        {
            var existing = await _db.Projects.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == p.Id, ct);

            if (existing is null)
            {
                existing = new Project { Id = p.Id };
                _db.Projects.Add(existing);
            }

            existing.WorkspaceId = p.WorkspaceId;
            existing.OwnerUserId = p.OwnerUserId;
            existing.Name = p.Name;
            existing.GithubRepoOwner = p.GithubRepoOwner;
            existing.GithubRepoName = p.GithubRepoName;
            existing.GithubInstallationId = p.GithubInstallationId;
            existing.PreviewPort = p.PreviewPort;
            existing.RuntimeCpuKind = p.RuntimeCpuKind;
            existing.RuntimeCpus = p.RuntimeCpus;
            existing.RuntimeMemoryMb = p.RuntimeMemoryMb;
            existing.RuntimeVolumeSizeGb = p.RuntimeVolumeSizeGb;
            existing.TemplateId = p.TemplateId;
            existing.Spec = p.Spec;
            existing.SpecVersion = p.SpecVersion;
            existing.IsDeleted = false;

            // ModelId has a private setter — go through the rich method.
            existing.SetModel(p.ModelId);

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
        }
    }

    private async Task ImportProjectBranchesAsync(
        EnvironmentSnapshotDto snapshot, EnvironmentImportSummary summary, CancellationToken ct)
    {
        foreach (var b in snapshot.ProjectBranches)
        {
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
        EnvironmentSnapshotDto snapshot, EnvironmentImportSummary summary, CancellationToken ct)
    {
        foreach (var s in snapshot.ProjectSecrets)
        {
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
            existing.CreatedBy = s.CreatedBy;
            existing.IsDeleted = false;
            summary.ProjectSecrets++;
        }
    }

    private async Task ImportProjectAgentPermissionsAsync(
        EnvironmentSnapshotDto snapshot, EnvironmentImportSummary summary, CancellationToken ct)
    {
        foreach (var a in snapshot.ProjectAgentPermissions)
        {
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
        EnvironmentSnapshotDto snapshot, EnvironmentImportSummary summary, CancellationToken ct)
    {
        foreach (var s in snapshot.Specifications)
        {
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
            entry.Property(nameof(Specification.CreatedBy)).CurrentValue = s.CreatedBy;
            existing.IsDeleted = false;
            summary.Specifications++;
        }
    }

    // ---------------- Kanban cards (+ subtasks) ----------------

    private async Task ImportKanbanCardsAsync(
        EnvironmentSnapshotDto snapshot, EnvironmentImportSummary summary, CancellationToken ct)
    {
        foreach (var c in snapshot.KanbanCards)
        {
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
            entry.Property(nameof(ProjectKanbanCard.CreatedBy)).CurrentValue = c.CreatedBy;
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
}

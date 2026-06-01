using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Source.Features.EnvironmentBackup.Models;
using Source.Features.Projects.Services;
using Source.Features.ProjectSecrets.Services;
using Source.Features.SystemSettings.Services;
using Source.Features.Users.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.EnvironmentBackup.Queries.ExportEnvironment;

/// <summary>
/// Reads all in-scope entities and decrypts every secret to clear text, producing the
/// single versioned <see cref="EnvironmentSnapshotDto"/>. Only the users actually
/// referenced by the exported rows are included so the blob stays focused on what a
/// restore needs to resolve FKs.
/// </summary>
public sealed class ExportEnvironmentHandler
    : IQueryHandler<ExportEnvironmentQuery, Result<EnvironmentSnapshotDto>>
{
    private readonly ApplicationDbContext _db;
    private readonly ISystemSettingsCipher _cipher;
    private readonly SecretEncryptionService _encryption;
    private readonly UserManager<User> _userManager;
    private readonly ILogger<ExportEnvironmentHandler> _logger;

    public ExportEnvironmentHandler(
        ApplicationDbContext db,
        ISystemSettingsCipher cipher,
        SecretEncryptionService encryption,
        UserManager<User> userManager,
        ILogger<ExportEnvironmentHandler> logger)
    {
        _db = db;
        _cipher = cipher;
        _encryption = encryption;
        _userManager = userManager;
        _logger = logger;
    }

    public async Task<Result<EnvironmentSnapshotDto>> Handle(
        ExportEnvironmentQuery request,
        CancellationToken ct)
    {
        var snapshot = new EnvironmentSnapshotDto
        {
            Version = EnvironmentSnapshotVersions.Current,
            ExportedAtUtc = DateTime.UtcNow,
        };

        // ---- SystemSettings (decrypt secrets to clear text) ----
        var settingRows = await _db.SystemSettings.AsNoTracking().ToListAsync(ct);
        foreach (var row in settingRows)
        {
            snapshot.SystemSettings.Add(new SystemSettingSnapshot
            {
                Key = row.Key,
                Category = row.Category,
                IsSecret = row.IsSecret,
                Value = DecryptSettingValue(row.Key, row.IsSecret, row.Value),
            });
        }

        // ---- Workspaces / memberships / invites / specs ----
        var workspaces = await _db.Workspaces.AsNoTracking().ToListAsync(ct);
        snapshot.Workspaces = workspaces.Select(w => new WorkspaceSnapshot
        {
            Id = w.Id,
            Slug = w.Slug,
            Name = w.Name,
            OwnerId = w.OwnerId,
        }).ToList();

        var memberships = await _db.WorkspaceMemberships.AsNoTracking().ToListAsync(ct);
        snapshot.WorkspaceMemberships = memberships.Select(m => new WorkspaceMembershipSnapshot
        {
            Id = m.Id,
            WorkspaceId = m.WorkspaceId,
            UserId = m.UserId,
            Role = (int)m.Role,
        }).ToList();

        var invites = await _db.WorkspaceInvites.AsNoTracking().ToListAsync(ct);
        snapshot.WorkspaceInvites = invites.Select(i => new WorkspaceInviteSnapshot
        {
            Id = i.Id,
            WorkspaceId = i.WorkspaceId,
            Email = i.Email,
            Role = (int)i.Role,
            InvitedById = i.InvitedById,
            Token = i.Token,
            ExpiresAt = i.ExpiresAt,
            AcceptedAt = i.AcceptedAt,
            AcceptedByUserId = i.AcceptedByUserId,
        }).ToList();

        var workspaceSpecs = await _db.WorkspaceSpecs.AsNoTracking().ToListAsync(ct);
        snapshot.WorkspaceSpecs = workspaceSpecs.Select(s => new WorkspaceSpecSnapshot
        {
            Id = s.Id,
            WorkspaceId = s.WorkspaceId,
            Name = s.Name,
            Description = s.Description,
            Content = s.Content,
            CreatedByUserId = s.CreatedByUserId,
            UpdatedByUserId = s.UpdatedByUserId,
        }).ToList();

        // ---- GitHub installations ----
        var installations = await _db.GithubInstallations.AsNoTracking().ToListAsync(ct);
        snapshot.GithubInstallations = installations.Select(g => new GithubInstallationSnapshot
        {
            Id = g.Id,
            WorkspaceId = g.WorkspaceId,
            InstallationId = g.InstallationId,
            AccountLogin = g.AccountLogin,
            AccountType = g.AccountType,
            AccountAvatarUrl = g.AccountAvatarUrl,
            Suspended = g.Suspended,
            UserAccessToken = g.UserAccessToken,
            UserAccessTokenExpiresAt = g.UserAccessTokenExpiresAt,
            UserRefreshToken = g.UserRefreshToken,
            UserRefreshTokenExpiresAt = g.UserRefreshTokenExpiresAt,
            UserLogin = g.UserLogin,
        }).ToList();

        // ---- Projects (decrypt cursor key) ----
        var projects = await _db.Projects.AsNoTracking().ToListAsync(ct);
        foreach (var p in projects)
        {
            snapshot.Projects.Add(new ProjectSnapshot
            {
                Id = p.Id,
                WorkspaceId = p.WorkspaceId,
                OwnerUserId = p.OwnerUserId,
                Name = p.Name,
                GithubRepoOwner = p.GithubRepoOwner,
                GithubRepoName = p.GithubRepoName,
                GithubInstallationId = p.GithubInstallationId,
                PreviewPort = p.PreviewPort,
                RuntimeCpuKind = p.RuntimeCpuKind,
                RuntimeCpus = p.RuntimeCpus,
                RuntimeMemoryMb = p.RuntimeMemoryMb,
                RuntimeVolumeSizeGb = p.RuntimeVolumeSizeGb,
                ModelId = p.ModelId,
                CursorApiKey = await DecryptCursorKeyAsync(p.Id, p.EncryptedCursorApiKey, ct),
                TemplateId = p.TemplateId,
                Spec = p.Spec,
                SpecVersion = p.SpecVersion,
            });
        }

        // ---- Project branches (metadata only) ----
        var branches = await _db.ProjectBranches.AsNoTracking().ToListAsync(ct);
        snapshot.ProjectBranches = branches.Select(b => new ProjectBranchSnapshot
        {
            Id = b.Id,
            ProjectId = b.ProjectId,
            Name = b.Name,
            IsDefault = b.IsDefault,
            IsArchived = b.IsArchived,
            ArchivedAt = b.ArchivedAt,
        }).ToList();

        // ---- Project secrets (decrypt to clear text) ----
        var secrets = await _db.ProjectSecrets.AsNoTracking().ToListAsync(ct);
        foreach (var s in secrets)
        {
            string plaintext;
            try
            {
                plaintext = await _encryption.DecryptAsync(s.ProjectId, s.Ciphertext, s.Nonce, s.DekVersion, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "ExportEnvironment: could not decrypt secret {Key} for project {ProjectId}; skipping.",
                    s.Key, s.ProjectId);
                continue;
            }

            snapshot.ProjectSecrets.Add(new ProjectSecretSnapshot
            {
                Id = s.Id,
                ProjectId = s.ProjectId,
                Key = s.Key,
                Value = plaintext,
                Version = s.Version,
                CreatedBy = s.CreatedBy,
            });
        }

        // ---- Project agent permissions ----
        var permissions = await _db.ProjectAgentPermissions.AsNoTracking().ToListAsync(ct);
        snapshot.ProjectAgentPermissions = permissions.Select(a => new ProjectAgentPermissionsSnapshot
        {
            Id = a.Id,
            ProjectId = a.ProjectId,
            PermissionMode = a.PermissionMode,
            AllowDangerouslySkipPermissions = a.AllowDangerouslySkipPermissions,
            AllowedTools = a.AllowedTools.ToList(),
            DisallowedTools = a.DisallowedTools.ToList(),
            AdditionalDirectories = a.AdditionalDirectories.ToList(),
        }).ToList();

        // ---- Specifications ----
        var specs = await _db.Specifications.AsNoTracking().ToListAsync(ct);
        snapshot.Specifications = specs.Select(s => new SpecificationSnapshot
        {
            Id = s.Id,
            ProjectId = s.ProjectId,
            Slug = s.Slug,
            Name = s.Name,
            Content = s.Content,
            Status = (int)s.Status,
            CreatedBy = s.CreatedBy,
        }).ToList();

        // ---- Kanban cards (+ subtasks) ----
        var cards = await _db.ProjectKanbanCards.AsNoTracking().ToListAsync(ct);
        var subtasks = await _db.ProjectKanbanCardSubtasks.AsNoTracking().ToListAsync(ct);
        var subtasksByCard = subtasks
            .GroupBy(t => t.ProjectKanbanCardId)
            .ToDictionary(g => g.Key, g => g.OrderBy(t => t.Position).ToList());

        foreach (var c in cards)
        {
            var cardDto = new KanbanCardSnapshot
            {
                Id = c.Id,
                ProjectId = c.ProjectId,
                Title = c.Title,
                Description = c.Description,
                Status = (int)c.Status,
                Position = c.Position,
                Priority = (int)c.Priority,
                DueDate = c.DueDate,
                CreatedBy = c.CreatedBy,
                Source = (int)c.Source,
                CreatedOnBranch = c.CreatedOnBranch,
            };

            if (subtasksByCard.TryGetValue(c.Id, out var cardSubtasks))
            {
                cardDto.Subtasks = cardSubtasks.Select(t => new KanbanSubtaskSnapshot
                {
                    Id = t.Id,
                    Title = t.Title,
                    IsCompleted = t.IsCompleted,
                    Position = t.Position,
                }).ToList();
            }

            snapshot.KanbanCards.Add(cardDto);
        }

        // ---- Referenced users (owners + members + inviters + creators) ----
        snapshot.Users = await BuildReferencedUsersAsync(snapshot, ct);

        _logger.LogInformation(
            "ExportEnvironment: assembled snapshot v{Version} — {Users} users, {Workspaces} workspaces, {Projects} projects, {Cards} cards.",
            snapshot.Version, snapshot.Users.Count, snapshot.Workspaces.Count,
            snapshot.Projects.Count, snapshot.KanbanCards.Count);

        return Result.Success(snapshot);
    }

    private string? DecryptSettingValue(string key, bool isSecret, string? value)
    {
        if (value is null) return null;
        if (!isSecret) return value;
        try
        {
            return _cipher.Decrypt(value);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ExportEnvironment: could not decrypt system setting {Key}; exporting as null.", key);
            return null;
        }
    }

    private async Task<string?> DecryptCursorKeyAsync(Guid projectId, string? envelope, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(envelope)) return null;
        try
        {
            var (ciphertext, nonce, dekVersion) = ProjectByokEnvelope.Unpack(envelope);
            return await _encryption.DecryptAsync(projectId, ciphertext, nonce, dekVersion, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ExportEnvironment: could not decrypt cursor key for project {ProjectId}; exporting as null.",
                projectId);
            return null;
        }
    }

    private async Task<List<ApplicationUserSnapshot>> BuildReferencedUsersAsync(
        EnvironmentSnapshotDto snapshot,
        CancellationToken ct)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        void Add(string? id)
        {
            if (!string.IsNullOrWhiteSpace(id)) ids.Add(id);
        }

        foreach (var w in snapshot.Workspaces) Add(w.OwnerId);
        foreach (var m in snapshot.WorkspaceMemberships) Add(m.UserId);
        foreach (var i in snapshot.WorkspaceInvites) { Add(i.InvitedById); Add(i.AcceptedByUserId); }
        foreach (var s in snapshot.WorkspaceSpecs) { Add(s.CreatedByUserId); Add(s.UpdatedByUserId); }
        foreach (var p in snapshot.Projects) Add(p.OwnerUserId);
        foreach (var s in snapshot.ProjectSecrets) Add(s.CreatedBy);
        foreach (var s in snapshot.Specifications) Add(s.CreatedBy);
        foreach (var c in snapshot.KanbanCards) Add(c.CreatedBy);

        if (ids.Count == 0) return new List<ApplicationUserSnapshot>();

        var users = await _db.Users
            .AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .ToListAsync(ct);

        var result = new List<ApplicationUserSnapshot>(users.Count);
        foreach (var u in users)
        {
            var roles = await _userManager.GetRolesAsync(u);
            result.Add(new ApplicationUserSnapshot
            {
                Id = u.Id,
                Email = u.Email,
                UserName = u.UserName,
                FirstName = u.FirstName,
                LastName = u.LastName,
                PasswordHash = u.PasswordHash,
                EmailConfirmed = u.EmailConfirmed,
                IsOnboarded = u.IsOnboarded,
                Credits = u.Credits,
                Roles = roles.ToList(),
            });
        }

        return result;
    }
}

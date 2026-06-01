namespace Source.Features.EnvironmentBackup.Models;

/// <summary>
/// The single combined JSON document produced by <c>GET /api/environment/export</c>
/// and consumed by <c>POST /api/environment/import</c>. One top-level list section per
/// in-scope entity type, plus a <see cref="Version"/> field so future format changes can
/// be detected and rejected gracefully.
///
/// <para><b>Secrets are CLEAR TEXT inside this blob.</b> The blob itself is the sensitive
/// artifact — the operator stores it in a password manager. On export, every secret is
/// decrypted (SystemSettings secret values, ProjectSecret values, Project cursor key); on
/// import, every secret is re-encrypted under the <i>target</i> environment's keys. See
/// the export query / import command handlers for the round-trip.</para>
///
/// <para><b>What's NOT here.</b> Live runtime state (Fly machines / volumes /
/// ProjectRuntime), Cloudflare tunnels / SubdomainAssignment, per-session runtime tokens,
/// conversation / agent / event history, FlyOperation audit records, and logs are all
/// re-provisioned automatically on boot and are deliberately excluded.</para>
/// </summary>
public sealed class EnvironmentSnapshotDto
{
    /// <summary>
    /// Snapshot format version. Starts at <c>"1"</c>. The import handler rejects any value
    /// it doesn't recognise so a newer blob can't be silently half-restored into an older
    /// build.
    /// </summary>
    public string Version { get; set; } = EnvironmentSnapshotVersions.Current;

    /// <summary>UTC timestamp the snapshot was taken. Informational only.</summary>
    public DateTime ExportedAtUtc { get; set; }

    /// <summary>System-level configuration rows (incl. GitHub App credentials, Fly token, runtime-token signing keys, Cloudflare token, webhook secrets). Secret values are clear text.</summary>
    public List<SystemSettingSnapshot> SystemSettings { get; set; } = new();

    /// <summary>Workspace owners + members referenced by the other sections. Relates to existing ASP.NET Identity users on import; identity schema is never recreated.</summary>
    public List<ApplicationUserSnapshot> Users { get; set; } = new();

    public List<WorkspaceSnapshot> Workspaces { get; set; } = new();
    public List<WorkspaceMembershipSnapshot> WorkspaceMemberships { get; set; } = new();
    public List<WorkspaceInviteSnapshot> WorkspaceInvites { get; set; } = new();
    public List<WorkspaceSpecSnapshot> WorkspaceSpecs { get; set; } = new();
    public List<GithubInstallationSnapshot> GithubInstallations { get; set; } = new();
    public List<ProjectSnapshot> Projects { get; set; } = new();
    public List<ProjectBranchSnapshot> ProjectBranches { get; set; } = new();
    public List<ProjectSecretSnapshot> ProjectSecrets { get; set; } = new();
    public List<ProjectAgentPermissionsSnapshot> ProjectAgentPermissions { get; set; } = new();
    public List<SpecificationSnapshot> Specifications { get; set; } = new();
    public List<KanbanCardSnapshot> KanbanCards { get; set; } = new();
}

/// <summary>Known snapshot format versions. Bump <see cref="Current"/> and add to <see cref="Supported"/> when the shape changes.</summary>
public static class EnvironmentSnapshotVersions
{
    public const string Current = "1";

    public static readonly IReadOnlyCollection<string> Supported = new[] { "1" };

    public static bool IsSupported(string? version) =>
        version is not null && Supported.Contains(version);
}

/// <summary>A single SystemSetting row. <see cref="Value"/> is clear text even when <see cref="IsSecret"/> is true.</summary>
public sealed class SystemSettingSnapshot
{
    public string Key { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool IsSecret { get; set; }
    public string? Value { get; set; }
}

/// <summary>
/// A referenced ASP.NET Identity user. Carries enough to resolve FKs and (when available)
/// preserve the login. Matched on <see cref="Id"/> with <see cref="Email"/> as fallback;
/// identity schema is never recreated.
/// </summary>
public sealed class ApplicationUserSnapshot
{
    public string Id { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? UserName { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }

    /// <summary>The Identity password hash if present on the source row. Restored verbatim so existing credentials keep working. Null when the source had none.</summary>
    public string? PasswordHash { get; set; }

    public bool EmailConfirmed { get; set; }
    public bool IsOnboarded { get; set; }
    public int Credits { get; set; }

    /// <summary>Role names the user holds (e.g. <c>SuperAdmin</c>). Re-applied on import where the role exists.</summary>
    public List<string> Roles { get; set; } = new();
}

public sealed class WorkspaceSnapshot
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
}

public sealed class WorkspaceMembershipSnapshot
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string UserId { get; set; } = string.Empty;

    /// <summary>Numeric <c>WorkspaceRole</c> (Owner=0, Admin=1, Member=2).</summary>
    public int Role { get; set; }
}

public sealed class WorkspaceInviteSnapshot
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Email { get; set; } = string.Empty;
    public int Role { get; set; }
    public string InvitedById { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public string? AcceptedByUserId { get; set; }
}

public sealed class WorkspaceSpecSnapshot
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Content { get; set; } = string.Empty;
    public string CreatedByUserId { get; set; } = string.Empty;
    public string UpdatedByUserId { get; set; } = string.Empty;
}

public sealed class GithubInstallationSnapshot
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public long InstallationId { get; set; }
    public string AccountLogin { get; set; } = string.Empty;
    public string AccountType { get; set; } = string.Empty;
    public string? AccountAvatarUrl { get; set; }
    public bool Suspended { get; set; }
    public string? UserAccessToken { get; set; }
    public DateTime? UserAccessTokenExpiresAt { get; set; }
    public string? UserRefreshToken { get; set; }
    public DateTime? UserRefreshTokenExpiresAt { get; set; }
    public string? UserLogin { get; set; }
}

public sealed class ProjectSnapshot
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string OwnerUserId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string GithubRepoOwner { get; set; } = string.Empty;
    public string GithubRepoName { get; set; } = string.Empty;
    public Guid? GithubInstallationId { get; set; }
    public int PreviewPort { get; set; }
    public string RuntimeCpuKind { get; set; } = string.Empty;
    public int RuntimeCpus { get; set; }
    public int RuntimeMemoryMb { get; set; }
    public int RuntimeVolumeSizeGb { get; set; }
    public Guid? ModelId { get; set; }

    /// <summary>BYOK Cursor API key in CLEAR TEXT. Re-encrypted under the target environment's project DEK on import. Null when the source had none.</summary>
    public string? CursorApiKey { get; set; }

    public Guid? TemplateId { get; set; }
    public string? Spec { get; set; }
    public int SpecVersion { get; set; }
}

public sealed class ProjectBranchSnapshot
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public bool IsArchived { get; set; }
    public DateTime? ArchivedAt { get; set; }
}

/// <summary>A project secret. <see cref="Value"/> is CLEAR TEXT; re-encrypted under the target environment's project DEK on import.</summary>
public sealed class ProjectSecretSnapshot
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public int Version { get; set; }
    public string? CreatedBy { get; set; }
}

public sealed class ProjectAgentPermissionsSnapshot
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string PermissionMode { get; set; } = string.Empty;
    public bool AllowDangerouslySkipPermissions { get; set; }
    public List<string> AllowedTools { get; set; } = new();
    public List<string> DisallowedTools { get; set; } = new();
    public List<string> AdditionalDirectories { get; set; } = new();
}

public sealed class SpecificationSnapshot
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public int Status { get; set; }
    public string? CreatedBy { get; set; }
}

public sealed class KanbanCardSnapshot
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int Status { get; set; }
    public int Position { get; set; }
    public int Priority { get; set; }
    public DateTime? DueDate { get; set; }
    public string? CreatedBy { get; set; }
    public int Source { get; set; }
    public string? CreatedOnBranch { get; set; }
    public List<KanbanSubtaskSnapshot> Subtasks { get; set; } = new();
}

public sealed class KanbanSubtaskSnapshot
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public bool IsCompleted { get; set; }
    public int Position { get; set; }
}

/// <summary>Per-entity-type counts returned after a successful import so the operator can confirm the restore.</summary>
public sealed class EnvironmentImportSummary
{
    public string Version { get; set; } = string.Empty;
    public int Users { get; set; }
    public int UsersWithoutPasswordHash { get; set; }
    public int SystemSettings { get; set; }
    public int Workspaces { get; set; }
    public int WorkspaceMemberships { get; set; }
    public int WorkspaceInvites { get; set; }
    public int WorkspaceSpecs { get; set; }
    public int GithubInstallations { get; set; }
    public int Projects { get; set; }
    public int ProjectBranches { get; set; }
    public int ProjectSecrets { get; set; }
    public int ProjectAgentPermissions { get; set; }
    public int Specifications { get; set; }
    public int KanbanCards { get; set; }
    public int KanbanSubtasks { get; set; }
}

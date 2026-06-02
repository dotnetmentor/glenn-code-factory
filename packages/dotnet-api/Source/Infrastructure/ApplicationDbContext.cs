using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Source.Features.Attachments.Models;
using Source.Features.CursorModels.Models;
using Source.Features.Cloudflare.Models;
using Source.Features.Conversations.Models;
using Source.Features.DaemonVersions.Models;
using Source.Features.FlyManagement.Models;
using Source.Features.GitHub.Models;
using Source.Features.GitOps.Models;
using Source.Features.Hooks.Models;
using Source.Features.Mcp.Models;
using Source.Features.ProjectKanban.Models;
using Source.Features.ProjectTemplates.Models;
using Source.Features.Projects.Models;
using Source.Features.Specifications.Models;
using Source.Features.ProjectSecrets.Models;
using Source.Features.RuntimeBootstrap.Models;
using Source.Features.RuntimeCuration.Models;
using Source.Features.RuntimeEvents.Models;
using Source.Features.RuntimeImages.Models;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.RuntimePresets.Models;
using Source.Features.RuntimeTokens.Models;
using Source.Features.SystemSettings.Models;
using Source.Features.Users.Models;
using Source.Features.ErrorLog.Models;
using Source.Features.Workspaces.Models;
using Source.Features.WorkspaceSpecs.Models;
using Source.Shared;
using Source.Shared.Events;

namespace Source.Infrastructure;

public class ApplicationDbContext : IdentityDbContext<User>
{
    private readonly IHttpContextAccessor? _httpContextAccessor;

    /// <summary>
    /// JSON serializer options used for <see cref="CursorModel"/> jsonb columns
    /// (<c>Aliases</c>, <c>Parameters</c>, <c>Variants</c>). CamelCase property
    /// names keep the on-disk shape symmetric with the <c>@cursor/sdk</c>
    /// payload the seed file came from, and with the wire format the frontend
    /// already speaks. Stable instance so EF's HasConversion lambdas don't
    /// allocate per row.
    /// </summary>
    internal static readonly JsonSerializerOptions CursorModelJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options, IHttpContextAccessor? httpContextAccessor = null) : base(options)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public DbSet<StoredDomainEvent> StoredDomainEvents { get; set; }
    public DbSet<StoredEntityChange> StoredEntityChanges { get; set; }
    public DbSet<ErrorLog> ErrorLogs { get; set; }
    public DbSet<ErrorSignature> ErrorSignatures { get; set; }
    public DbSet<Workspace> Workspaces { get; set; }
    public DbSet<WorkspaceMembership> WorkspaceMemberships { get; set; }
    public DbSet<WorkspaceInvite> WorkspaceInvites { get; set; }
    public DbSet<WorkspaceSpec> WorkspaceSpecs { get; set; } = null!;
    public DbSet<GithubInstallation> GithubInstallations { get; set; }
    public DbSet<GithubRepository> GithubRepositories { get; set; }
    public DbSet<GithubUserIdentity> GithubUserIdentities { get; set; }
    public DbSet<GithubWebhookDelivery> GithubWebhookDeliveries { get; set; }
    public DbSet<SystemSetting> SystemSettings { get; set; }
    public DbSet<FlyOperation> FlyOperations { get; set; }
    public DbSet<RuntimeImage> RuntimeImages { get; set; }
    public DbSet<BootstrapRun> BootstrapRuns { get; set; }
    public DbSet<ProjectRuntime> ProjectRuntimes { get; set; }
    public DbSet<RuntimeProposal> RuntimeProposals { get; set; }
    public DbSet<RuntimeStateEvent> RuntimeStateEvents { get; set; }
    public DbSet<RuntimeErrorReport> RuntimeErrorReports { get; set; }
    public DbSet<RuntimeEvent> RuntimeEvents { get; set; } = null!;
    public DbSet<ServicePreset> ServicePresets { get; set; } = null!;
    public DbSet<Source.Features.Health.Models.RuntimeDiskPressureEvent> RuntimeDiskPressureEvents { get; set; } = null!;
    public DbSet<RuntimeTokenIssue> RuntimeTokenIssues { get; set; }
    public DbSet<Conversation> Conversations { get; set; }
    public DbSet<AgentSession> AgentSessions { get; set; }
    public DbSet<AgentEvent> AgentEvents { get; set; }
    public DbSet<RunResult> RunResults { get; set; }
    public DbSet<HookExecution> HookExecutions { get; set; } = null!;
    public DbSet<RuntimeHookConfig> RuntimeHookConfigs { get; set; } = null!;
    public DbSet<GitOperation> GitOperations { get; set; } = null!;
    public DbSet<RuntimeGitConfig> RuntimeGitConfigs { get; set; } = null!;
    public DbSet<ProjectSecret> ProjectSecrets { get; set; } = null!;
    public DbSet<ProjectKeyMaterial> ProjectKeyMaterials { get; set; } = null!;
    public DbSet<WorkspaceKeyMaterial> WorkspaceKeyMaterials { get; set; } = null!;
    public DbSet<SecretAuditEvent> SecretAuditEvents { get; set; } = null!;
    public DbSet<McpServer> McpServers { get; set; } = null!;
    public DbSet<McpCall> McpCalls { get; set; } = null!;
    public DbSet<ProjectKanbanCard> ProjectKanbanCards { get; set; } = null!;
    public DbSet<ProjectKanbanCardSubtask> ProjectKanbanCardSubtasks { get; set; } = null!;
    public DbSet<Specification> Specifications { get; set; } = null!;
    public DbSet<Project> Projects { get; set; } = null!;
    public DbSet<ProjectBranch> ProjectBranches { get; set; } = null!;
    public DbSet<ProjectTemplate> ProjectTemplates { get; set; } = null!;
    public DbSet<ProjectAgentPermissions> ProjectAgentPermissions { get; set; } = null!;
    public DbSet<DaemonVersion> DaemonVersions { get; set; } = null!;
    public DbSet<SubdomainAssignment> SubdomainAssignments { get; set; } = null!;
    public DbSet<CursorModel> CursorModels { get; set; } = null!;
    public DbSet<Attachment> Attachments { get; set; } = null!;

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var userId = _httpContextAccessor?.HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier);

        foreach (var entry in ChangeTracker.Entries<IAuditable>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }

        foreach (var entry in ChangeTracker.Entries<ISoftDelete>())
        {
            if (entry.State == EntityState.Modified && entry.Entity.IsDeleted && entry.Property(nameof(ISoftDelete.IsDeleted)).IsModified)
            {
                entry.Entity.DeletedAt = now;
                entry.Entity.DeletedBy = userId;
            }
        }

        return await base.SaveChangesAsync(cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // User soft-delete filter
        builder.Entity<User>().HasQueryFilter(e => !e.IsDeleted);

        // StoredDomainEvent configuration
        builder.Entity<StoredDomainEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EventType).IsRequired().HasMaxLength(256);
            entity.Property(e => e.Payload).IsRequired().HasColumnType("jsonb");
            entity.Property(e => e.EntityId).HasMaxLength(256);
            entity.Property(e => e.EntityType).HasMaxLength(256);
            entity.Property(e => e.UserId).HasMaxLength(256);
            entity.HasIndex(e => e.EventType);
            entity.HasIndex(e => e.OccurredAt);
            entity.HasIndex(e => new { e.EntityType, e.EntityId });
        });

        // StoredEntityChange configuration
        builder.Entity<StoredEntityChange>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EntityType).IsRequired().HasMaxLength(256);
            entity.Property(e => e.EntityId).IsRequired().HasMaxLength(256);
            entity.Property(e => e.Operation).IsRequired().HasMaxLength(50);
            entity.Property(e => e.ChangedProperties).IsRequired().HasColumnType("jsonb");
            entity.Property(e => e.UserId).HasMaxLength(256);
            entity.HasIndex(e => e.EntityType);
            entity.HasIndex(e => e.OccurredAt);
            entity.HasIndex(e => new { e.EntityType, e.EntityId });
        });

        // ErrorSignature configuration — aggregated fingerprint rows.
        builder.Entity<ErrorSignature>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Hash).IsRequired().HasMaxLength(64);
            entity.Property(e => e.Source).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Severity).IsRequired().HasMaxLength(20);
            entity.Property(e => e.FirstSeenAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.LastSeenAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.ResolvedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");

            // Unique index on Hash — prevents duplicate signature rows.
            entity.HasIndex(e => e.Hash).IsUnique();

            // Composite index for dashboard queries ("most recently seen, unresolved").
            // DESC on LastSeenAt is applied via raw SQL in the migration because EF Core's
            // relational index API does not expose per-column sort order in 9.0.
            entity.HasIndex(e => new { e.LastSeenAt, e.IsResolved })
                .HasDatabaseName("IX_ErrorSignatures_LastSeenAt_IsResolved");
        });

        // -------- Workspaces --------

        builder.Entity<Workspace>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Slug).IsRequired().HasMaxLength(60);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(120);
            entity.Property(e => e.OwnerId).IsRequired().HasMaxLength(450);
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.DeletedAt).HasColumnType("timestamp with time zone");
            entity.HasIndex(e => e.Slug).IsUnique();
            entity.HasIndex(e => e.OwnerId);
            entity.HasOne(e => e.Owner)
                .WithMany()
                .HasForeignKey(e => e.OwnerId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(e => e.EncryptedCursorApiKey);
            entity.Property(e => e.AllowProjectCursorApiKeyOverride).HasDefaultValue(true);
            entity.HasQueryFilter(e => !e.IsDeleted);
        });

        builder.Entity<WorkspaceMembership>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).IsRequired().HasMaxLength(450);
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");
            // Unique constraint: a user appears at most once per workspace.
            entity.HasIndex(e => new { e.WorkspaceId, e.UserId }).IsUnique();
            entity.HasIndex(e => e.UserId);
            entity.HasOne(e => e.Workspace)
                .WithMany(w => w.Memberships)
                .HasForeignKey(e => e.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<WorkspaceInvite>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Email).IsRequired().HasMaxLength(256);
            entity.Property(e => e.Token).IsRequired().HasMaxLength(128);
            entity.Property(e => e.InvitedById).IsRequired().HasMaxLength(450);
            entity.Property(e => e.AcceptedByUserId).HasMaxLength(450);
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.ExpiresAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.AcceptedAt).HasColumnType("timestamp with time zone");
            entity.HasIndex(e => e.Token).IsUnique();
            // We can't easily express "unique on (workspace, email) where AcceptedAt is null" via
            // the EF model API portably; we enforce it in handlers. For lookup speed we still index.
            entity.HasIndex(e => new { e.WorkspaceId, e.Email });
            entity.HasOne(e => e.Workspace)
                .WithMany(w => w.Invites)
                .HasForeignKey(e => e.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.InvitedBy)
                .WithMany()
                .HasForeignKey(e => e.InvitedById)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // -------- WorkspaceSpecs --------
        // Workspace-scoped catalog of named, reusable V2 runtime specs. The
        // catalog is a stamp, not a live link: forking a branch / creating a
        // project that picks a catalog spec deep-copies Content into the new
        // runtime's Spec, so subsequent edits or deletions here never affect
        // existing branches. (snapshot semantic — see WorkspaceSpec.cs)
        builder.Entity<WorkspaceSpec>(entity =>
        {
            entity.ToTable("WorkspaceSpecs");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Description).HasMaxLength(500);

            // jsonb so handlers can index / partial-update later if needed;
            // matches ProjectRuntime.Spec column type for shape parity.
            entity.Property(e => e.Content).IsRequired().HasColumnType("jsonb");

            // User FK columns — string, not Guid, to match ASP.NET Identity's
            // string user PK convention used by every other user reference in
            // the schema (Workspace.OwnerId, WorkspaceMembership.UserId, etc.).
            // 450 = the same max length AspNet Identity uses for its user PK.
            entity.Property(e => e.CreatedByUserId).IsRequired().HasMaxLength(450);
            entity.Property(e => e.UpdatedByUserId).IsRequired().HasMaxLength(450);

            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");

            // Unique catalog name per workspace — two entries in the same
            // workspace can't share a name. Also serves as the dominant lookup
            // index for "list specs in workspace X".
            entity.HasIndex(e => new { e.WorkspaceId, e.Name })
                .IsUnique()
                .HasDatabaseName("IX_WorkspaceSpecs_WorkspaceId_Name");

            // FK to Workspace — Cascade so deleting a workspace deletes its
            // catalog. Existing branches forked from those entries are
            // unaffected because the spec was copied in, not linked. Mirrors
            // the WorkspaceMembership / WorkspaceInvite cascade convention.
            entity.HasOne(e => e.Workspace)
                .WithMany()
                .HasForeignKey(e => e.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ErrorLog -> ErrorSignature FK. Nullable on the ErrorLog side so existing rows
        // continue to exist and new rows can be enqueued before the signature is assigned.
        // DeleteBehavior.SetNull: deleting a signature orphans the samples rather than
        // cascading; signatures are small and high-signal, so we'd rarely delete them,
        // but if we do, keeping the samples as history is the safer default.
        builder.Entity<ErrorLog>(entity =>
        {
            entity.HasIndex(e => e.SignatureId);
            entity.HasOne(e => e.Signature)
                .WithMany()
                .HasForeignKey(e => e.SignatureId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // -------- GitHub integration --------

        builder.Entity<GithubInstallation>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.AccountLogin).IsRequired().HasMaxLength(120);
            entity.Property(e => e.AccountType).IsRequired().HasMaxLength(32);
            entity.Property(e => e.AccountAvatarUrl).HasMaxLength(500);
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");
            // User-OAuth token columns — see GithubInstallation.cs for the rationale.
            // Tokens themselves have no documented max length; GitHub's `ghu_…` /
            // `ghr_…` are ~40 chars today but we leave plenty of headroom for any
            // future expansion. UserLogin matches the AccountLogin column constraint.
            entity.Property(e => e.UserAccessToken).HasMaxLength(255);
            entity.Property(e => e.UserAccessTokenExpiresAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UserRefreshToken).HasMaxLength(255);
            entity.Property(e => e.UserRefreshTokenExpiresAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UserLogin).HasMaxLength(120);
            // Each GitHub installation belongs to exactly one workspace at a time.
            entity.HasIndex(e => e.InstallationId).IsUnique();
            entity.HasIndex(e => e.WorkspaceId);
            entity.HasOne(e => e.Workspace)
                .WithMany()
                .HasForeignKey(e => e.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<GithubRepository>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Owner).IsRequired().HasMaxLength(120);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(120);
            entity.Property(e => e.FullName).IsRequired().HasMaxLength(255);
            entity.Property(e => e.DefaultBranch).HasMaxLength(255);
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.LastSyncedAt).HasColumnType("timestamp with time zone");
            // Composite uniqueness — a repo can only appear once per installation.
            entity.HasIndex(e => new { e.GithubInstallationId, e.GithubRepoId }).IsUnique();
            entity.HasIndex(e => e.GithubInstallationId);
            entity.HasOne(e => e.Installation)
                .WithMany(i => i.Repositories)
                .HasForeignKey(e => e.GithubInstallationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<GithubUserIdentity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).IsRequired().HasMaxLength(450);
            entity.Property(e => e.GithubLogin).IsRequired().HasMaxLength(120);
            entity.Property(e => e.AvatarUrl).HasMaxLength(500);
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");
            // Both directions of the link are unique — one app user ↔ one GitHub identity.
            entity.HasIndex(e => e.UserId).IsUnique();
            entity.HasIndex(e => e.GithubUserId).IsUnique();
            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<GithubWebhookDelivery>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.DeliveryId).IsRequired().HasMaxLength(128);
            entity.Property(e => e.Event).IsRequired().HasMaxLength(64);
            entity.Property(e => e.Action).HasMaxLength(64);
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");
            // Idempotency lookup — refuse to process the same delivery id twice.
            entity.HasIndex(e => e.DeliveryId).IsUnique();
            entity.HasIndex(e => new { e.Event, e.Action });
        });

        // -------- SystemSettings --------
        // The Key is the natural unique identifier (e.g. "GitHub:AppId") and serves as PK.
        // Category is indexed for the per-category lazy-load query that powers the cache.
        builder.Entity<SystemSetting>(entity =>
        {
            entity.HasKey(e => e.Key);
            entity.Property(e => e.Key).HasMaxLength(200);
            entity.Property(e => e.Category).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Description).HasMaxLength(2000);
            entity.Property(e => e.UpdatedBy).HasMaxLength(450);
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");
            entity.HasIndex(e => e.Category);
        });

        // -------- FlyManagement --------
        // Append-only audit log of every Fly machines API call. No soft delete, no FK to
        // Runtime — RuntimeId is just a Guid we record, since runtimes can come and go
        // but the audit trail must outlive them.
        builder.Entity<FlyOperation>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Operation).IsRequired().HasMaxLength(100);
            entity.Property(e => e.RequestKey).HasMaxLength(200);
            entity.Property(e => e.RequestPayload).IsRequired().HasColumnType("jsonb");
            entity.Property(e => e.ResponsePayload).HasColumnType("jsonb");
            entity.Property(e => e.ErrorCode).HasMaxLength(100);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");

            // "Latest ops for this runtime" — the dominant read pattern. DESC on
            // CreatedAt is applied via raw SQL in the migration since EF Core 9
            // doesn't expose per-column sort order on relational indexes.
            entity.HasIndex(e => new { e.RuntimeId, e.CreatedAt })
                .HasDatabaseName("IX_FlyOperations_RuntimeId_CreatedAt");

            // Idempotency lookup. Non-unique on purpose: multiple attempts (Pending →
            // Failed → retry → Succeeded) legitimately share the same key, and the
            // caller picks the latest succeeded row.
            entity.HasIndex(e => e.RequestKey)
                .HasDatabaseName("IX_FlyOperations_RequestKey");
        });

        // -------- RuntimeImages --------
        // Catalog of every published runtime base image. No soft delete — yanked
        // images stay in the table so the audit trail survives. Tag is the
        // natural idempotency key for CI registration.
        builder.Entity<RuntimeImage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Tag).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Digest).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Registry).IsRequired().HasMaxLength(300);
            entity.Property(e => e.GitSha).IsRequired().HasMaxLength(64);
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(e => e.BuiltAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");

            // CI must not publish the same tag twice — this is also the natural
            // idempotency key the registration endpoint uses.
            entity.HasIndex(e => e.Tag).IsUnique();

            // "Latest active images" — the dominant read pattern (default spawn
            // selection). DESC on BuiltAt is applied via raw SQL in the migration
            // since EF Core 9 doesn't expose per-column sort order on relational
            // indexes.
            entity.HasIndex(e => new { e.Status, e.BuiltAt })
                .HasDatabaseName("IX_RuntimeImages_Status_BuiltAt");
        });

        // -------- RuntimeBootstrap --------
        // Append-only audit log of every bootstrap attempt. No soft delete, no FK
        // to Runtime — RuntimeId is just a Guid we record, since runtimes can be
        // torn down but the audit trail must outlive them. Mirrors FlyOperation.
        builder.Entity<BootstrapRun>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FinalStage).HasConversion<string>().HasMaxLength(40).IsRequired();
            entity.Property(e => e.ErrorReason).HasMaxLength(4000);
            entity.Property(e => e.DaemonVersion).HasMaxLength(64);
            entity.Property(e => e.ImageDigest).HasMaxLength(128);
            entity.Property(e => e.BootstrapVersion).IsRequired().HasMaxLength(16);
            entity.Property(e => e.StartedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.EndedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");

            // "Latest boots for this runtime" — the dominant read pattern. DESC
            // on StartedAt is applied via raw SQL in the migration since EF Core 9
            // doesn't expose per-column sort order on relational indexes.
            entity.HasIndex(e => new { e.RuntimeId, e.StartedAt })
                .HasDatabaseName("IX_BootstrapRuns_RuntimeId_StartedAt");

            // "Show failed boots" filter — small, high-cardinality flag, but the
            // index keeps the scan cheap when most boots succeed.
            entity.HasIndex(e => e.Success)
                .HasDatabaseName("IX_BootstrapRuns_Success");
        });

        // -------- Projects --------
        // The connected GitHub repo inside a Workspace. The unit a user "opens"
        // to start work — every ProjectRuntime / Conversation / kanban card /
        // secret hangs off a project. Soft-deletable + auditable, mirrors
        // Workspace. WorkspaceId is the tenancy boundary; OwnerUserId is the
        // IdentityUser id of the creator (string, not Guid — Identity user
        // keys are strings; matches Workspace.OwnerId). The GithubInstallation
        // FK identifies which install token mints clone / API access for the
        // repo.
        builder.Entity<Project>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.OwnerUserId).IsRequired().HasMaxLength(450);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.GithubRepoOwner).IsRequired().HasMaxLength(120);
            entity.Property(e => e.GithubRepoName).IsRequired().HasMaxLength(120);

            // BYOK envelopes — base64-encoded JSON wrapping ciphertext + nonce
            // + dekVersion. Both nullable: absence means "no per-project key".
            // No length cap (text); a typical Anthropic key envelope is ~200
            // bytes so the column stays small in practice. Plaintext NEVER
            // lives in the DB — see SecretEncryptionService for the AES-GCM
            // envelope format and ProjectByokEnvelope for the wrapping helper.

            // PreviewPort (cloudflare-tunnel-preview Phase 2) — per-project
            // dev-server port, default 5173 (Vite). HasDefaultValue ensures the
            // migration backfills existing rows with 5173 instead of 0; the
            // CLR-side initializer (Project.DefaultPreviewPort) protects new
            // rows that bypass DB defaults. Range validation
            // (1..65535) lives on Project.SetPreviewPort so the entity owns the
            // invariant.
            entity.Property(e => e.PreviewPort)
                .IsRequired()
                .HasDefaultValue(Project.DefaultPreviewPort);

            // Per-project runtime spec — Fly machine sizing used when spawning
            // any *new* ProjectRuntime under this project. HasDefaultValue makes
            // the EF migration backfill existing rows with the conservative
            // shared-cpu-1x / 2 GiB / 5 GiB tuple, matching the historical
            // MachineGuest() defaults so nothing changes for projects that
            // haven't opted into a custom spec yet.
            entity.Property(e => e.RuntimeCpuKind)
                .IsRequired()
                .HasMaxLength(16)
                .HasDefaultValue(Project.DefaultRuntimeCpuKind);
            entity.Property(e => e.RuntimeCpus)
                .IsRequired()
                .HasDefaultValue(Project.DefaultRuntimeCpus);
            entity.Property(e => e.RuntimeMemoryMb)
                .IsRequired()
                .HasDefaultValue(Project.DefaultRuntimeMemoryMb);
            entity.Property(e => e.RuntimeVolumeSizeGb)
                .IsRequired()
                .HasDefaultValue(Project.DefaultRuntimeVolumeSizeGb);

            // Project-level runtime spec — source of truth for what's
            // installed across every runtime under this project. Moved from
            // ProjectRuntime.Spec per the `project-level-runtime-spec` spec.
            // jsonb to match the historical column shape on ProjectRuntime.
            // Null on Phase 1 / pre-curation projects; treated as empty by
            // readers (same defensive policy as the old per-runtime column).
            entity.Property(e => e.Spec).HasColumnType("jsonb");

            // Bump-on-write counter for Spec. Default 1 so existing rows
            // backfilled by the migration land at the historical baseline.
            entity.Property(e => e.SpecVersion)
                .HasColumnType("integer")
                .HasDefaultValue(1)
                .IsRequired();

            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.DeletedAt).HasColumnType("timestamp with time zone");

            // "Projects in workspace X" — dominant lookup for the workspace
            // landing page / project picker.
            entity.HasIndex(e => e.WorkspaceId)
                .HasDatabaseName("IX_Projects_WorkspaceId");

            // "Projects I own" — used by the user's home page.
            entity.HasIndex(e => e.OwnerUserId)
                .HasDatabaseName("IX_Projects_OwnerUserId");

            // Audit / dashboard lookups by GitHub installation.
            entity.HasIndex(e => e.GithubInstallationId)
                .HasDatabaseName("IX_Projects_GithubInstallationId");

            // FK to Workspace — Cascade mirrors GithubInstallation.WorkspaceId
            // and WorkspaceMembership.WorkspaceId: a workspace owns its
            // projects, so a hard delete cascades. Soft-delete on Workspace
            // is the normal lifecycle path; the cascade is the safety net for
            // hard admin deletes.
            entity.HasOne(e => e.Workspace)
                .WithMany()
                .HasForeignKey(e => e.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);

            // FK to User on OwnerUserId — Restrict so deleting a user doesn't
            // cascade-nuke their projects (the workspace ownership-transfer
            // flow is the right place to reassign). Mirrors Workspace.OwnerId.
            entity.HasOne(e => e.Owner)
                .WithMany()
                .HasForeignKey(e => e.OwnerUserId)
                .OnDelete(DeleteBehavior.Restrict);

            // FK to GithubInstallation — nullable + SetNull. Disconnecting an
            // installation gracefully detaches its projects (FK -> NULL) rather
            // than blocking the disconnect with a Restrict explosion. A detached
            // project is readable but can't talk to GitHub until it's rejoined
            // to a fresh installation via the ReconnectProjects flow (matches by
            // GithubRepoOwner == GithubInstallation.AccountLogin).
            entity.HasOne(e => e.GithubInstallation)
                .WithMany()
                .HasForeignKey(e => e.GithubInstallationId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.Model)
                .WithMany()
                .HasForeignKey(e => e.ModelId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(e => e.ModelId)
                .HasDatabaseName("IX_Projects_ModelId");

            entity.Property(e => e.EncryptedCursorApiKey).HasColumnType("text");

            // Optional originating starter — historical FK to ProjectTemplate
            // (UI name "Starter"). SetNull so archiving / hard-deleting a
            // starter never breaks projects that were born from it; existing
            // rows that pre-date the catalogue stay null. Purely additive: the
            // existing CreateProject flow remains a no-op against this column.
            // Indexed for the admin "which projects came from starter X?"
            // lookup.
            entity.HasOne(e => e.Template)
                .WithMany()
                .HasForeignKey(e => e.TemplateId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(e => e.TemplateId)
                .HasDatabaseName("IX_Projects_TemplateId");

            // Soft-delete filter — match the Workspace pattern so default
            // queries skip Deleted rows; admin paths use IgnoreQueryFilters().
            entity.HasQueryFilter(e => !e.IsDeleted);
        });

        // -------- ProjectTemplates --------
        // Curated global catalogue of project starters. GLOBAL scope — there
        // is intentionally NO WorkspaceId column; starters are super-admin
        // curated and shared across all workspaces. Mirrors WorkspaceSpec
        // conventions (IAuditable + ISoftDelete) but NOT its tenancy.
        // RuntimeSpec is stored inline as jsonb (not an FK to WorkspaceSpec)
        // so the catalogue stays global while preserving the snapshot-on-
        // create-project semantics every existing path already relies on.
        builder.Entity<ProjectTemplate>(entity =>
        {
            entity.ToTable("ProjectTemplates");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Slug).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.IconKey).HasMaxLength(50);
            entity.Property(e => e.SourceRepoOwner).IsRequired().HasMaxLength(120);
            entity.Property(e => e.SourceRepoName).IsRequired().HasMaxLength(120);

            // Inline V2 runtime-spec document. Nullable — the "Empty" starter
            // (and any starter created without a curated recipe) carries null
            // here, which the create-project flow treats as "use the default/
            // empty runtime spec". Same column type as ProjectRuntime.Spec /
            // WorkspaceSpec.Content so handler-level JSON validators can be
            // shared across all three sites in a follow-up card.
            entity.Property(e => e.RuntimeSpec).HasColumnType("jsonb");

            entity.Property(e => e.IsActive).IsRequired().HasDefaultValue(true);
            entity.Property(e => e.IsDefault).IsRequired().HasDefaultValue(false);
            entity.Property(e => e.SortOrder).IsRequired().HasDefaultValue(0);

            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.DeletedAt).HasColumnType("timestamp with time zone");

            // Slug is unique among non-tombstoned rows so an admin can recreate
            // a slug after a soft-delete without a DB conflict. Same partial-
            // index pattern as Workspaces, AgentModels, SubdomainAssignments.
            entity.HasIndex(e => e.Slug)
                .IsUnique()
                .HasFilter("\"IsDeleted\" = false")
                .HasDatabaseName("IX_ProjectTemplates_Slug");

            // Dominant picker query — "active starters, ordered by SortOrder".
            // Keeps the new-project screen lookup index-only.
            entity.HasIndex(e => new { e.IsActive, e.SortOrder })
                .HasDatabaseName("IX_ProjectTemplates_IsActive_SortOrder");

            // Soft-delete query filter — admin paths use IgnoreQueryFilters().
            entity.HasQueryFilter(e => !e.IsDeleted);
        });

        // -------- ProjectBranches --------
        // First-class branch row inside a Project. Runtime granularity in the
        // e2e-smoketest spec is (Project, ProjectBranch) — branches are no
        // longer modelled as free-form strings on ProjectRuntime / Conversation.
        // Auditable but NOT soft-deletable: runtimes / conversations are pinned
        // to a branch for life, so deleting the branch row would orphan FKs.
        // Lifecycle is "create on demand, never delete in v1".
        builder.Entity<ProjectBranch>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Name).IsRequired().HasMaxLength(250);

            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");

            // Archive flags. Default false / null at the DB level so existing
            // rows backfill cleanly without a data migration. ArchivedAt is
            // paired with IsArchived: null whenever IsArchived=false,
            // populated when IsArchived flips true.
            entity.Property(e => e.IsArchived).HasDefaultValue(false);
            entity.Property(e => e.ArchivedAt).HasColumnType("timestamp with time zone");

            // "One branch row per (project, branch name)" — uniqueness invariant.
            // Doubles as the dominant lookup index ("does this branch exist on
            // project X yet?" during runtime / conversation creation).
            entity.HasIndex(e => new { e.ProjectId, e.Name })
                .IsUnique()
                .HasDatabaseName("IX_ProjectBranches_ProjectId_Name");

            // Filtering listings on (ProjectId, IsArchived) is the dominant
            // sidebar / branch-picker query now that archive lands — this
            // index keeps the common "active branches for project X" lookup
            // index-only without touching the table heap.
            entity.HasIndex(e => new { e.ProjectId, e.IsArchived })
                .HasDatabaseName("IX_ProjectBranches_ProjectId_IsArchived");

            // FK to Project — Cascade mirrors the Project ↔ ProjectRuntime
            // relationship: a project owns its branches, so a hard delete
            // cascades. Soft-delete on Project is the normal lifecycle path;
            // the cascade is the safety net for hard admin deletes.
            entity.HasOne(e => e.Project)
                .WithMany(p => p.Branches)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // -------- ProjectAgentPermissions --------
        // Project-scoped override of the system Agent SDK permission
        // defaults. Presence of the row IS the override — the resolver
        // service returns these values verbatim with NO merging against
        // the system catalog (see agent-sdk-permissions spec). Absence
        // of the row means "fall through to system defaults". 1:0..1 to
        // Project, enforced by a unique index on ProjectId.
        //
        // Auditable but NOT soft-deletable: this is config, not data.
        // Hard-deleting an override is the supported "stop overriding"
        // gesture, and a hard project delete cascades the row away.
        builder.Entity<ProjectAgentPermissions>(entity =>
        {
            entity.HasKey(e => e.Id);

            // SDK enum stored as string — see entity XML doc for why.
            // Cap matches the longest SDK mode name with plenty of slack
            // ("bypassPermissions" = 17 chars today).
            entity.Property(e => e.PermissionMode).IsRequired().HasMaxLength(64);

            // jsonb columns for the three list fields. Npgsql's dynamic
            // JSON serialiser is enabled in DatabaseExtensions, so plain
            // List<string> round-trips natively against a jsonb column.
            // jsonb (rather than text[]) keeps the storage shape symmetric
            // with how the SystemSettings.AgentPermissions catalog stores
            // the same fields, simplifying the resolver service.
            entity.Property(e => e.AllowedTools).HasColumnType("jsonb");
            entity.Property(e => e.DisallowedTools).HasColumnType("jsonb");
            entity.Property(e => e.AdditionalDirectories).HasColumnType("jsonb");

            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");

            // Unique index on ProjectId enforces the 1:0..1 relationship
            // at the database level — a project cannot have two override
            // rows, even under a write race.
            entity.HasIndex(e => e.ProjectId)
                .IsUnique()
                .HasDatabaseName("IX_ProjectAgentPermissions_ProjectId");

            // 1:0..1 to Project. Cascade so a hard-deleted project takes
            // its override row with it; a soft-deleted project leaves the
            // row in place (which is fine — nothing reads it while the
            // project is soft-deleted).
            entity.HasOne(e => e.Project)
                .WithOne(p => p.AgentPermissions)
                .HasForeignKey<ProjectAgentPermissions>(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // -------- DaemonVersions --------
        // Versioned daemon bundles served to runtime containers on cold-boot.
        // Auditable but NOT soft-deletable: old versions stay around as
        // immutable history (cheap rows, useful for forensics + rollback).
        // Exactly one row per channel may have IsActive=true at a time —
        // enforced at write time by PublishDaemonVersionHandler inside a
        // single SaveChanges (= transaction).
        builder.Entity<DaemonVersion>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Version).IsRequired().HasMaxLength(64);
            entity.Property(e => e.Channel).IsRequired().HasMaxLength(32);
            entity.Property(e => e.BundleStorageKey).IsRequired().HasMaxLength(1024);
            entity.Property(e => e.BundleSha256).IsRequired().HasMaxLength(64);
            entity.Property(e => e.Notes).HasMaxLength(2000);

            entity.Property(e => e.ReleasedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");

            // "One Version row per (channel, version)" — uniqueness invariant
            // (the timestamp-based version generator already produces unique
            // strings, but this is the belt-and-braces DB guarantee).
            entity.HasIndex(e => new { e.Channel, e.Version })
                .IsUnique()
                .HasDatabaseName("IX_DaemonVersions_Channel_Version");

            // Dominant lookup: "what's the active version for channel X?" —
            // hit on every runtime cold-boot via /api/daemon-versions/resolve.
            entity.HasIndex(e => new { e.Channel, e.IsActive })
                .HasDatabaseName("IX_DaemonVersions_Channel_IsActive");
        });

        // -------- RuntimeLifecycle --------
        // Central record per project tracking the Fly machine + volume and the
        // current lifecycle state. Soft-deletable (Deleted state is a 30-day
        // window before janitor hard-delete). ProjectId is now a real FK to
        // Project (promoted in the e2e-smoketest spec from a plain Guid).
        builder.Entity<ProjectRuntime>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.State).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(e => e.Region).IsRequired().HasMaxLength(16);

            // Spec-health (self-healing-runtime-specs). Persisted as a string
            // (same readability-over-compactness convention as State above) so
            // adding new health members later doesn't shift ordinals on existing
            // rows. DB default 'Unknown' backfills pre-feature rows and any row
            // the daemon hasn't reported on yet. The column already exists on the
            // platform DB (43594/app) — the recovery migration's idempotent
            // ADD COLUMN IF NOT EXISTS keeps fresh runtime DBs in sync.
            entity.Property(e => e.SpecHealth)
                .HasConversion<string>()
                .HasMaxLength(16)
                .IsRequired()
                .HasDefaultValue(RuntimeSpecHealth.Unknown);
            entity.Property(e => e.FlyMachineId).HasMaxLength(64);
            entity.Property(e => e.FlyVolumeId).HasMaxLength(64);
            entity.Property(e => e.ImageDigest).HasMaxLength(128);

            entity.Property(e => e.StateChangedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.LastHeartbeatAt).HasColumnType("timestamp with time zone");
            // Bootstrap-liveness watchdog signal (self-healing-runtime-specs, card
            // B4). Bumped on each mid-boot RuntimeEvent so HeartbeatWatcherJob can
            // measure silence instead of stale UpdatedAt. Column already exists on
            // the platform DB (43594/app); the recovery migration's idempotent
            // ADD COLUMN IF NOT EXISTS keeps fresh runtime DBs in sync.
            entity.Property(e => e.LastBootstrapActivityAt).HasColumnType("timestamp with time zone");

            // Self-healing repair consent (self-healing-runtime-specs, B2/B3).
            // The bool/int columns carry explicit DB defaults so the idempotent
            // recovery migration's ADD COLUMN IF NOT EXISTS and the model snapshot
            // agree on the backfill for pre-feature rows. The two timestamps are
            // plain nullable tz columns. All already exist on 43594/app.
            entity.Property(e => e.AutoApplyNextProposal)
                .IsRequired()
                .HasDefaultValue(false);
            entity.Property(e => e.AutoApplyExpiresAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.AutoApplyAttemptsRemaining)
                .IsRequired()
                .HasDefaultValue(0);
            entity.Property(e => e.RepairAttempts)
                .IsRequired()
                .HasDefaultValue(0);
            entity.Property(e => e.LastRepairAttemptAt).HasColumnType("timestamp with time zone");

            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.DeletedAt).HasColumnType("timestamp with time zone");

            // NOTE: The per-runtime Spec / SpecVersion columns moved to Project
            // per the `project-level-runtime-spec` spec. See Project entity
            // configuration above.

            // -------- Observability snapshots (super-admin polish) --------
            //
            // Latest heartbeat-pushed snapshots. Stored on the runtime row
            // (not in RuntimeEvent) because they're per-tick samples — the
            // drawer reads "current state" once on open + over SignalR for
            // live updates; persisting every sample would blow past the
            // 5000-event cap inside an hour.
            entity.Property(e => e.LastDiskSampledAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.LastSysstatsSnapshot).HasColumnType("jsonb");
            entity.Property(e => e.LastSupervisordSnapshot).HasColumnType("jsonb");

            // Per-runtime Fly machine spec — snapshotted from Project at row
            // creation. HasDefaultValue backfills the conservative tuple on
            // existing rows so nothing in production changes spec mid-life.
            entity.Property(e => e.CpuKind)
                .IsRequired()
                .HasMaxLength(16)
                .HasDefaultValue("shared");
            entity.Property(e => e.Cpus)
                .IsRequired()
                .HasDefaultValue(1);
            entity.Property(e => e.MemoryMb)
                .IsRequired()
                .HasDefaultValue(2048);

            // "Find the runtime for project X" — dominant lookup.
            entity.HasIndex(e => e.ProjectId)
                .HasDatabaseName("IX_ProjectRuntimes_ProjectId");

            // Background workers scan by current state ("all Online runtimes",
            // "all Crashed runtimes awaiting respawn", etc.).
            entity.HasIndex(e => e.State)
                .HasDatabaseName("IX_ProjectRuntimes_State");

            // Resolve "which runtime is this Fly webhook about?" by machine id.
            entity.HasIndex(e => e.FlyMachineId)
                .HasDatabaseName("IX_ProjectRuntimes_FlyMachineId");

            // FK to Project on ProjectId — Cascade mirrors the Workspace ↔
            // Project relationship: a project owns its runtimes, so a hard
            // delete cascades. The soft-delete query filter handles the
            // normal lifecycle (Deleted runtimes stay in the table for the
            // 30-day janitor window); cascade is the hard-delete safety net.
            entity.HasOne(e => e.Project)
                .WithMany(p => p.Runtimes)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            // "Find the runtime for (project, branch)" — promoted alongside the
            // branch FK in the e2e-smoketest spec. Runtimes are pinned to a
            // branch for life.
            entity.HasIndex(e => e.BranchId)
                .HasDatabaseName("IX_ProjectRuntimes_BranchId");

            // FK to ProjectBranch on BranchId — Restrict because a runtime is
            // pinned to one (Project, Branch) pair for life. The branch row is
            // never deleted in v1 (see ProjectBranch comment); Restrict makes
            // any future "delete a branch with live runtimes" attempt surface
            // the dependency rather than silently orphan rows.
            entity.HasOne(e => e.Branch)
                .WithMany(b => b.Runtimes)
                .HasForeignKey(e => e.BranchId)
                .OnDelete(DeleteBehavior.Restrict);

            // Soft-delete filter — match the Workspace pattern so default
            // queries skip Deleted rows; the janitor uses IgnoreQueryFilters().
            entity.HasQueryFilter(e => !e.IsDeleted);
        });

        // -------- RuntimeCuration (RuntimeProposal) --------
        // One row per `propose_runtime_spec` tool call from the daemon. Soft-
        // deletable so noisy proposals can be hidden without losing the audit
        // trail — mirrors HookExecution / GitOperation. FK to ProjectRuntime
        // on RuntimeId with NoAction because runtimes are soft-deleted and the
        // proposal history must outlive the runtime row within the 30-day
        // janitor window. ProjectId is a plain Guid (no FK) — Project entity
        // is owned by another slice. ProposedSpec / AppliedSpec are jsonb;
        // Status is persisted as int (small finite enum, mirrors HookPoint /
        // SecretAuditAction rather than RuntimeState's string-on-disk choice).
        // Composite indexes on (ProjectId, CreatedAt DESC) and
        // (RuntimeId, CreatedAt DESC) — DESC variants are emitted via raw SQL
        // in the migration since EF Core 9 doesn't expose per-column sort
        // order on relational indexes.
        builder.Entity<RuntimeProposal>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Status).HasConversion<int>();
            entity.Property(e => e.ProposedSpec).HasColumnType("jsonb").IsRequired();
            entity.Property(e => e.ExpandedSpec).HasColumnType("jsonb");
            entity.Property(e => e.AppliedSpec).HasColumnType("jsonb");
            entity.Property(e => e.Reason).HasColumnType("text");
            entity.Property(e => e.ErrorMessage).HasColumnType("text");
            entity.Property(e => e.DecidedBy).HasMaxLength(450);
            // Apply-timing columns — stamped by RecordApplyResultCommandHandler
            // when the daemon's ack lands. jsonb on PhaseTimings to match the
            // structured-payload-as-text convention used by ProposedSpec /
            // AppliedSpec / RuntimeEvent.Payload.
            entity.Property(e => e.PhaseTimings).HasColumnType("jsonb");

            entity.Property(e => e.DecidedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.DeletedAt).HasColumnType("timestamp with time zone");

            // "Pending proposals" — small set, but the dashboard / approval
            // queue scans by Status frequently enough to warrant the index.
            entity.HasIndex(e => e.Status)
                .HasDatabaseName("IX_RuntimeProposals_Status");

            // FK to ProjectRuntime — NoAction because we soft-delete runtimes
            // and the proposal history must outlive the runtime row. Mirrors
            // HookExecution / GitOperation rather than the spec hint of
            // Cascade (Cascade would nuke the audit trail on runtime delete).
            entity.HasOne(e => e.Runtime)
                .WithMany()
                .HasForeignKey(e => e.RuntimeId)
                .OnDelete(DeleteBehavior.NoAction);

            // Soft-delete filter — match the HookExecution / ProjectRuntime
            // / Workspace pattern so default queries skip deleted rows; admin
            // paths use IgnoreQueryFilters() to see everything.
            entity.HasQueryFilter(e => !e.IsDeleted);
        });

        // -------- RuntimeStateEvent --------
        // Append-only audit log of every lifecycle state transition. No soft
        // delete and no FK to ProjectRuntime — RuntimeId is just a Guid we
        // record, since runtimes can be hard-deleted but the audit trail
        // must outlive them. Mirrors FlyOperation / BootstrapRun.
        builder.Entity<RuntimeStateEvent>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.FromState).HasConversion<string>().HasMaxLength(32);
            entity.Property(e => e.ToState).IsRequired().HasConversion<string>().HasMaxLength(32);
            entity.Property(e => e.Reason).IsRequired().HasMaxLength(256);
            entity.Property(e => e.TriggeredBy).IsRequired().HasMaxLength(128);
            entity.Property(e => e.Metadata).HasMaxLength(4000);

            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");

            // "Latest transitions for this runtime" — the dominant read pattern.
            // DESC on CreatedAt is applied via raw SQL in the migration since
            // EF Core 9 doesn't expose per-column sort order on relational
            // indexes. The fluent index here is a placeholder so EF tracks the
            // columns; the migration drops it and re-emits the DESC variant.
            entity.HasIndex(e => new { e.RuntimeId, e.CreatedAt })
                .HasDatabaseName("IX_RuntimeStateEvents_RuntimeId_CreatedAt");

            // "All transitions into state X" — used by dashboards / alerting.
            entity.HasIndex(e => e.ToState)
                .HasDatabaseName("IX_RuntimeStateEvents_ToState");
        });

        // -------- RuntimeErrorReport --------
        // Append-only audit row for daemon-reported errors. Same shape rules as
        // BootstrapRun / RuntimeStateEvent: no FK to ProjectRuntime (runtimes
        // can be hard-deleted; the audit trail must outlive them) and no soft
        // delete (hiding diagnostic rows defeats the point). The dominant read
        // is "show the last N errors for this runtime, newest first" — DESC on
        // CreatedAt is applied via raw SQL in the migration since EF Core 9
        // doesn't expose per-column sort order on relational indexes.
        builder.Entity<RuntimeErrorReport>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Category).IsRequired().HasMaxLength(64);
            entity.Property(e => e.Message).IsRequired().HasMaxLength(4000);
            entity.Property(e => e.StackTrace).HasMaxLength(16000);
            entity.Property(e => e.Context).HasMaxLength(16000);

            entity.Property(e => e.ReportedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");

            // "Latest errors for this runtime" — dominant lookup. Composite
            // index; the DESC-on-CreatedAt variant is re-emitted via raw SQL
            // in the migration.
            entity.HasIndex(e => new { e.RuntimeId, e.CreatedAt })
                .HasDatabaseName("IX_RuntimeErrorReports_RuntimeId_CreatedAt");

            // "Error frequency by category" — small, but cheap to keep around
            // for the admin dashboard.
            entity.HasIndex(e => e.Category)
                .HasDatabaseName("IX_RuntimeErrorReports_Category");
        });

        // -------- RuntimeEvent --------
        // Append-only structured event store backing the runtime drawer's
        // Timeline tab. Same shape rules as RuntimeStateEvent / RuntimeErrorReport:
        // no FK to ProjectRuntime, no soft-delete. Retention is enforced by
        // RecordRuntimeEventCommandHandler's rolling per-runtime cap (5000)
        // rather than a time-based janitor — the volume per runtime is small
        // enough that FIFO-by-count gives a predictable working set.
        //
        // Three indexes cover the dominant query patterns:
        //   1. (RuntimeId, Timestamp DESC) — the Timeline tab's default scroll.
        //   2. (RuntimeId, Type, Timestamp DESC) — filtered Timeline ("only
        //      InstallFailed events").
        //   3. (RuntimeId, DurationMs DESC) WHERE DurationMs IS NOT NULL — the
        //      "slowest installs" / "slowest setup commands" lookups. Partial
        //      so we don't pay storage on the *Started rows that legitimately
        //      have no duration.
        //
        // DESC sort order on Timestamp / DurationMs is applied via raw SQL in
        // the migration; EF Core 9 doesn't expose per-column sort order on
        // relational indexes. The fluent forms here are placeholders so EF
        // tracks the columns.
        builder.Entity<RuntimeEvent>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Type)
                .IsRequired()
                .HasMaxLength(64);

            entity.Property(e => e.Severity)
                .IsRequired()
                .HasConversion<string>()
                .HasMaxLength(16);

            entity.Property(e => e.Timestamp)
                .HasColumnType("timestamp with time zone");

            entity.Property(e => e.Payload)
                .IsRequired()
                .HasColumnType("jsonb");

            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");

            // Timeline default: "last N events for this runtime, newest first".
            entity.HasIndex(e => new { e.RuntimeId, e.Timestamp })
                .HasDatabaseName("IX_RuntimeEvents_RuntimeId_Timestamp");

            // Filtered Timeline: "InstallFailed events for this runtime".
            entity.HasIndex(e => new { e.RuntimeId, e.Type, e.Timestamp })
                .HasDatabaseName("IX_RuntimeEvents_RuntimeId_Type_Timestamp");

            // "Slowest events" — partial index, non-null DurationMs only. The
            // partial predicate is emitted via raw SQL in the migration since
            // HasFilter() goes there cleanly.
            entity.HasIndex(e => new { e.RuntimeId, e.DurationMs })
                .HasDatabaseName("IX_RuntimeEvents_RuntimeId_DurationMs")
                .HasFilter("\"DurationMs\" IS NOT NULL");
        });

        // -------- ServicePreset (Runtime Spec V3) --------
        // DB-backed preset registry replacing the V2 hardcoded gallery. Each
        // row holds a CommandTemplate + EnvTemplate + parameter schema; the
        // PresetExpander renders {{handlebars}} placeholders at proposal time
        // to produce the daemon-bound ServiceSpec. Built-in rows seeded by
        // the AddServicePresetsV3 migration; admin clones land alongside.
        builder.Entity<ServicePreset>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Slug).IsRequired().HasMaxLength(64);
            entity.Property(e => e.DisplayName).IsRequired().HasMaxLength(128);
            entity.Property(e => e.Description).IsRequired().HasMaxLength(1024);

            // Persist as int — same on-disk convention as RuntimeProposal.Status.
            // Pinned enum values keep the column stable across reorderings.
            entity.Property(e => e.Category).HasConversion<int>();

            entity.Property(e => e.IconName).HasMaxLength(64);

            entity.Property(e => e.CommandTemplate).IsRequired().HasMaxLength(4096);

            // jsonb on Postgres — Dictionary<string,string> serialised by
            // callers (PresetExpander). String-on-CLR matches Project.Spec /
            // RuntimeProposal.ProposedSpec / RuntimeEvent.Payload.
            entity.Property(e => e.EnvTemplate)
                .IsRequired()
                .HasColumnType("jsonb");

            entity.Property(e => e.HealthcheckCommand).HasMaxLength(1024);
            entity.Property(e => e.DefaultUser).HasMaxLength(64);

            entity.Property(e => e.InstallContribution).HasMaxLength(4096);
            entity.Property(e => e.SetupContribution).HasMaxLength(4096);
            entity.Property(e => e.InstallVerify).HasMaxLength(1024);

            // jsonb on Postgres — nullable List<RequiredEnvVar> serialised by
            // callers (PresetExpander). String-on-CLR matches EnvTemplate /
            // Parameters. Nullable so existing presets (which declare nothing)
            // round-trip as a JSON null with no migration data fixup.
            entity.Property(e => e.RequiredEnvContribution)
                .HasColumnType("jsonb");

            // jsonb on Postgres — List<PresetParameter> serialised by callers.
            entity.Property(e => e.Parameters)
                .IsRequired()
                .HasColumnType("jsonb");

            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.DeletedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.DeletedBy).HasMaxLength(450);

            // Soft-delete filter — retired presets stay queryable from audit
            // contexts but vanish from the gallery / agent tool description.
            entity.HasQueryFilter(e => !e.IsDeleted);

            // Slug uniqueness — the agent's tool input schema enumerates these
            // and PresetExpander does Slug → Preset lookups at proposal time.
            entity.HasIndex(e => e.Slug)
                .IsUnique()
                .HasDatabaseName("IX_ServicePresets_Slug");

            // Gallery default scroll: Category ASC then DisplayName ASC —
            // groups the picker into Backend / Frontend / Database sections.
            entity.HasIndex(e => new { e.Category, e.DisplayName })
                .HasDatabaseName("IX_ServicePresets_Category_DisplayName");
        });

        // -------- RuntimeDiskPressureEvent --------
        // Append-only audit row for daemon-reported disk-pressure transitions
        // (Phase D Card 3). Same shape rules as RuntimeErrorReport: no FK to
        // ProjectRuntime (audit must outlive the runtime row), no soft delete,
        // composite (RuntimeId, CreatedAt DESC) emitted via raw SQL because
        // EF Core 9 doesn't expose per-column sort order on relational indexes.
        builder.Entity<Source.Features.Health.Models.RuntimeDiskPressureEvent>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Level).IsRequired().HasMaxLength(16);

            entity.Property(e => e.SampledAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.ReportedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");

            // "Disk-pressure timeline for this runtime" — dominant lookup. The
            // DESC-on-CreatedAt variant is re-emitted via raw SQL in the
            // migration, same idiom as IX_RuntimeErrorReports_RuntimeId_CreatedAt.
            entity.HasIndex(e => new { e.RuntimeId, e.CreatedAt })
                .HasDatabaseName("IX_RuntimeDiskPressureEvents_RuntimeId_CreatedAt");
        });

        // -------- RuntimeTokenIssue --------
        // Append-only audit row, one per minted RuntimeToken JWT. NOT IAuditable
        // and NOT ISoftDelete — IssuedAt / RevokedAt are domain timestamps the
        // service sets explicitly. No FK to ProjectRuntime / Project / Tenant /
        // Branch: the audit trail must outlive any of those rows being hard-
        // deleted, mirroring the FlyOperation / BootstrapRun / RuntimeStateEvent
        // convention. Id doubles as the JWT jti claim, so the validate path
        // does the revocation check by primary key (no secondary index needed).
        builder.Entity<RuntimeTokenIssue>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Scope).HasMaxLength(64);
            e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired(); // sha256 hex = 64 chars
            e.Property(x => x.RevocationReason).HasMaxLength(256);
            e.HasIndex(x => x.RuntimeId);
            e.HasIndex(x => x.TenantId);
            // Composite for the rotation scan: "tokens nearing expiry that aren't revoked"
            e.HasIndex(x => new { x.ExpiresAt, x.RevokedAt });
            // Admin "recently used" listing — sparse non-null filter would be ideal
            // but EF/Postgres handles null-trailing on a btree fine for our scan
            // shape ("ORDER BY LastUsedAt DESC NULLS LAST LIMIT N"). The flush job
            // is the only writer of this column, so write contention is bounded.
            e.HasIndex(x => x.LastUsedAt);
        });

        // -------- Conversations --------
        // The user's thread of intent inside a project + branch. Plain Guid
        // ProjectId (no FK) — Project entity is owned by a future spec.
        // BranchId is now a real FK to ProjectBranch (promoted from a free-form
        // string in the e2e-smoketest spec). Lifecycle is tracked via Status —
        // there's no ISoftDelete; archive is the lifecycle flag and the global
        // query filter hides archived rows from default queries.
        builder.Entity<Conversation>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(e => e.IsAutoTitled).HasDefaultValue(true);

            entity.Property(e => e.LastActivityAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.ArchivedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");

            // Dominant lookup: "give me the conversations on (project, branch)".
            // The DESC composite on LastActivityAt is added as raw SQL in the
            // migration since EF Core 9 doesn't expose per-column sort order.
            entity.HasIndex(e => new { e.ProjectId, e.BranchId })
                .HasDatabaseName("IX_Conversations_ProjectId_BranchId");

            // Sidebar list order: "newest activity first on (project, branch)".
            // Created as raw SQL in the migration with DESC on LastActivityAt
            // — EF Core 9 still doesn't expose per-column sort direction.
            // EF doesn't track this index in the model; it's a hand-managed
            // covering index used by the conversation-list query.

            // "Archived filter" — fast scan for the sidebar's "Show archived"
            // toggle and any admin "what got archived recently" report.
            entity.HasIndex(e => e.ArchivedAt)
                .HasDatabaseName("IX_Conversations_ArchivedAt");

            // FK to ProjectBranch on BranchId — Restrict so deleting a branch
            // with live conversations surfaces the dependency rather than
            // silently orphaning rows. Branches are never deleted in v1, so in
            // practice this is the safety net.
            entity.HasOne(e => e.Branch)
                .WithMany(b => b.Conversations)
                .HasForeignKey(e => e.BranchId)
                .OnDelete(DeleteBehavior.Restrict);

            // Hide archived rows from default queries; admin paths use
            // IgnoreQueryFilters() to see everything.
            entity.HasQueryFilter(e => e.Status != ConversationStatus.Archived);
        });

        // -------- AgentSessions --------
        // One round-trip of agent work inside a conversation. Cascade-delete
        // from Conversation so tearing a conversation down removes its sessions
        // (and transitively their events). RuntimeId is denormalized from
        // Conversation.ProjectId → ProjectRuntime so the per-runtime dispatch
        // query (Card 3 of agent-execution-control) can hit a single composite
        // index without joining through Conversation. FK with NoAction —
        // runtimes are soft-deleted, the session audit trail outlives the
        // runtime row, mirroring HookExecution / GitOperation.
        builder.Entity<AgentSession>(entity =>
        {
            entity.HasKey(e => e.Id);

            // Prompt is unbounded user text — Postgres "text" column.
            entity.Property(e => e.Prompt).HasColumnType("text").IsRequired();
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(e => e.FailureReason).HasMaxLength(1000);
            entity.Property(e => e.CancelReason).HasMaxLength(256);
            entity.Property(e => e.AgentId).HasColumnType("text");

            entity.Property(e => e.StartedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.CompletedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");

            entity.HasOne(e => e.Conversation)
                .WithMany(c => c.Sessions)
                .HasForeignKey(e => e.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);

            // FK to ProjectRuntime — NoAction because we soft-delete runtimes
            // and the session audit trail must outlive the runtime row.
            entity.HasOne(e => e.Runtime)
                .WithMany()
                .HasForeignKey(e => e.RuntimeId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasOne(e => e.Model)
                .WithMany()
                .HasForeignKey(e => e.ModelId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(e => e.ModelId)
                .HasDatabaseName("IX_AgentSessions_ModelId");

            // Cost / usage metrics — denormalized onto the session at terminal
            // state from the SDK's `result` frame for cheap rollups up the
            // Conversation → Branch → Project → Workspace chain. All nullable;
            // null means "session pre-dates cost tracking" or "canceled before
            // SDK emitted a result frame". numeric(12,8) is plenty of headroom
            // — Anthropic's costliest published Opus turn is ~$2 so 12,8 gives
            // four whole digits and 8-decimal precision (the SDK reports cost
            // with up to ~6 significant digits).
            entity.Property(e => e.TotalCostUsd).HasPrecision(12, 8);

            // "Sessions of this conversation in order" — the chat-replay query.
            entity.HasIndex(e => new { e.ConversationId, e.CreatedAt })
                .HasDatabaseName("IX_AgentSessions_ConversationId_CreatedAt");

            // Per-runtime dispatch query: "for this runtime, what's the next
            // Pending session by QueuePosition?" — Card 3 of agent-execution
            // -control. Composite covers the WHERE + ORDER BY in one shot.
            entity.HasIndex(e => new { e.RuntimeId, e.Status, e.QueuePosition })
                .HasDatabaseName("IX_AgentSessions_Runtime_Status_QueuePosition");
        });

        // -------- AgentEvents (Cursor-native shape) --------
        // Append-only audit rows, one per Cursor SDK message the daemon
        // emitted. Single table with a Kind discriminator + per-kind nullable
        // promoted columns. Composite PK on (SessionId, Sequence) clusters
        // events of a session together for efficient range reads ("give me
        // events 100..200 of session X") and is the safety net for monotonic
        // ordering — the chat panel reads in Sequence asc, regardless of Kind.
        // Args / Result are jsonb because tool payload shape varies per tool;
        // every other field is first-class so the panel + analytics don't
        // parse JSON in the query plan.
        builder.Entity<AgentEvent>(entity =>
        {
            entity.HasKey(e => new { e.SessionId, e.Sequence });

            // Shared columns
            entity.Property(e => e.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");

            // ToolUse columns
            entity.Property(e => e.CallId).HasMaxLength(128);
            entity.Property(e => e.ToolName).HasMaxLength(128);
            entity.Property(e => e.ToolStatus).HasConversion<string>().HasMaxLength(32);
            entity.Property(e => e.Args).HasColumnType("jsonb");
            entity.Property(e => e.Result).HasColumnType("jsonb");

            // Status columns
            entity.Property(e => e.RunStatus).HasConversion<string>().HasMaxLength(32);

            // Task columns
            entity.Property(e => e.TaskId).HasMaxLength(128);
            entity.Property(e => e.TaskTitle).HasMaxLength(500);

            // Text (Thinking / AssistantText / PromptReceived) — unbounded.
            // No HasMaxLength so EF maps to text rather than varchar(n).

            entity.HasOne(e => e.Session)
                .WithMany(s => s.Events)
                .HasForeignKey(e => e.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // -------- RunResults --------
        // Per-turn aggregate row (one per AgentSession) capturing Cursor SDK's
        // RunResult — duration, model, optional git provenance, and the list
        // of artifacts as jsonb. Read by the chat panel's turn footer; written
        // when the daemon emits the terminal RunResult frame. PK == FK to
        // AgentSession with cascade — one result per session, immutable once
        // written.
        builder.Entity<RunResult>(entity =>
        {
            entity.HasKey(e => e.SessionId);

            entity.Property(e => e.Model).HasMaxLength(128).IsRequired();
            entity.Property(e => e.GitBranch).HasMaxLength(256);
            entity.Property(e => e.GitPrUrl).HasMaxLength(1024);
            entity.Property(e => e.ArtifactsJson).HasColumnType("jsonb").IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");

            entity.HasOne(e => e.Session)
                .WithOne(s => s.RunResult)
                .HasForeignKey<RunResult>(e => e.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // -------- Attachments (chat-file-attachments) --------
        builder.Entity<Attachment>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.FileName).HasMaxLength(Attachment.MaxFileNameLength).IsRequired();
            entity.Property(e => e.ContentType).HasMaxLength(Attachment.MaxContentTypeLength);
            entity.Property(e => e.R2Key).HasMaxLength(Attachment.MaxR2KeyLength).IsRequired();

            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UploadedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.StagedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.DeletedAt).HasColumnType("timestamp with time zone");

            // "Attachments for conversation X" — dominant lookup for the chat
            // panel's past-message rendering and any per-conversation cleanup.
            entity.HasIndex(e => e.ConversationId)
                .HasDatabaseName("IX_Attachments_ConversationId");

            entity.HasOne(e => e.Conversation)
                .WithMany()
                .HasForeignKey(e => e.ConversationId)
                .OnDelete(DeleteBehavior.Restrict);

            // Chat is event-sourced — the "user message" of a turn is the
            // PromptReceived AgentEvent at Sequence=0 of an AgentSession.
            // Linking attachments to the session id (set at SubmitPrompt time)
            // lets past-message chip rendering pull "attachments for session X"
            // without a separate junction table. Null until the prompt is sent.
            // SetNull on delete: sessions are not hard-deleted in v1 but if
            // that ever changes the attachment row stays reachable through
            // ConversationId.
            entity.HasOne(e => e.Session)
                .WithMany()
                .HasForeignKey(e => e.SessionId)
                .OnDelete(DeleteBehavior.SetNull);

            // "Attachments belonging to session X" — past-message chip render.
            // Filtered on non-null because draft-composer rows have no session
            // yet and we want the index dense.
            entity.HasIndex(e => e.SessionId)
                .HasDatabaseName("IX_Attachments_SessionId")
                .HasFilter("\"SessionId\" IS NOT NULL");

            entity.HasQueryFilter(e => !e.IsDeleted);
        });

        // -------- Hooks (HookExecution) --------
        // One row per hook invocation on a runtime. FK to ProjectRuntime on
        // RuntimeId with NoAction — runtimes are soft-deleted, so the hook
        // history must outlive the runtime row within the 30-day window.
        // ConversationId / TurnId are plain Guids (no FK) — same outlive-the
        // -row reasoning as FlyOperation.RuntimeId / BootstrapRun.RuntimeId.
        // Soft-deletable so noisy entries can be hidden without losing the
        // underlying audit trail.
        builder.Entity<HookExecution>(e =>
        {
            e.HasKey(x => x.Id);

            e.Property(x => x.HookName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Cmd).HasMaxLength(2000).IsRequired();
            e.Property(x => x.OutputTail).HasMaxLength(16384).IsRequired();
            e.Property(x => x.OutputHash).HasMaxLength(64).IsRequired();

            e.Property(x => x.HookPoint).HasConversion<int>();
            e.Property(x => x.FeedbackMode).HasConversion<int>();

            e.Property(x => x.StartedAt).HasColumnType("timestamp with time zone");
            e.Property(x => x.EndedAt).HasColumnType("timestamp with time zone");
            e.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
            e.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");
            e.Property(x => x.DeletedAt).HasColumnType("timestamp with time zone");

            // "Hooks for runtime X" — dominant lookup.
            e.HasIndex(x => x.RuntimeId);

            // "Latest hooks for this runtime" — timeline view.
            e.HasIndex(x => new { x.RuntimeId, x.StartedAt })
                .HasDatabaseName("IX_HookExecutions_Runtime_StartedAt");

            // "Hooks that ran for this conversation" — chat-side lookup.
            e.HasIndex(x => x.ConversationId);

            // FK to ProjectRuntime — NoAction because we soft-delete runtimes
            // and the hook history must outlive the runtime row.
            e.HasOne<Source.Features.RuntimeLifecycle.Models.ProjectRuntime>()
                .WithMany()
                .HasForeignKey(x => x.RuntimeId)
                .OnDelete(DeleteBehavior.NoAction);

            // Soft-delete filter — match the ProjectRuntime / Workspace
            // pattern so default queries skip deleted rows; admin paths use
            // IgnoreQueryFilters() to see everything.
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        // -------- Hooks (RuntimeHookConfig) --------
        // One row per runtime carrying the daemon's hook config as jsonb. The
        // server treats the body as opaque — only the admin endpoint validates
        // the top-level shape on write. Unique index on RuntimeId enforces the
        // one-row-per-runtime invariant. FK to ProjectRuntime with NoAction
        // mirrors HookExecution: runtimes are soft-deleted, stale config rows
        // are harmless, the janitor's 30-day window cleans up.
        builder.Entity<RuntimeHookConfig>(e =>
        {
            e.HasKey(x => x.Id);

            e.HasIndex(x => x.RuntimeId).IsUnique();

            e.Property(x => x.Json).HasColumnType("jsonb").IsRequired();

            e.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
            e.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");
            e.Property(x => x.DeletedAt).HasColumnType("timestamp with time zone");

            e.HasOne<Source.Features.RuntimeLifecycle.Models.ProjectRuntime>()
                .WithMany()
                .HasForeignKey(x => x.RuntimeId)
                .OnDelete(DeleteBehavior.NoAction);

            e.HasQueryFilter(x => !x.IsDeleted);
        });

        // -------- GitOps (GitOperation) --------
        // One row per git invocation on a runtime. FK to ProjectRuntime on
        // RuntimeId with NoAction — runtimes are soft-deleted, so the git
        // history must outlive the runtime row within the 30-day window.
        // ConversationId / TurnId are plain Guids (no FK) — same outlive-the
        // -row reasoning as HookExecution / FlyOperation / BootstrapRun.
        // Soft-deletable so noisy entries can be hidden without losing the
        // underlying audit trail. Mirrors HookExecution stylistically — the
        // two tables have the same shape because they answer the same kind
        // of question for different subsystems.
        builder.Entity<GitOperation>(e =>
        {
            e.HasKey(x => x.Id);

            e.Property(x => x.CommandLine).HasMaxLength(2000).IsRequired();
            e.Property(x => x.OutputTail).HasMaxLength(16384).IsRequired();
            e.Property(x => x.OutputHash).HasMaxLength(64).IsRequired();

            e.Property(x => x.OpType).HasConversion<int>();

            e.Property(x => x.StartedAt).HasColumnType("timestamp with time zone");
            e.Property(x => x.EndedAt).HasColumnType("timestamp with time zone");
            e.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
            e.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");
            e.Property(x => x.DeletedAt).HasColumnType("timestamp with time zone");

            // "Git ops for runtime X" — dominant lookup.
            e.HasIndex(x => x.RuntimeId);

            // "Latest git ops for this runtime" — timeline view.
            e.HasIndex(x => new { x.RuntimeId, x.StartedAt })
                .HasDatabaseName("IX_GitOperations_Runtime_StartedAt");

            // "Git ops that ran for this conversation" — chat-side lookup.
            e.HasIndex(x => x.ConversationId);

            // FK to ProjectRuntime — NoAction because we soft-delete runtimes
            // and the git history must outlive the runtime row.
            e.HasOne<Source.Features.RuntimeLifecycle.Models.ProjectRuntime>()
                .WithMany()
                .HasForeignKey(x => x.RuntimeId)
                .OnDelete(DeleteBehavior.NoAction);

            // Soft-delete filter — match the HookExecution / ProjectRuntime /
            // Workspace pattern so default queries skip deleted rows; admin
            // paths use IgnoreQueryFilters() to see everything.
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        // -------- GitOps (RuntimeGitConfig) --------
        // One row per runtime carrying the daemon's git config: AutoCommit
        // toggle + optional SSH deploy key (plaintext for v1; TODO encrypt
        // at rest once the secrets feature lands). Unique index on RuntimeId
        // enforces the one-row-per-runtime invariant. FK to ProjectRuntime
        // with NoAction mirrors RuntimeHookConfig: runtimes are soft-deleted,
        // stale config rows are harmless, the janitor's 30-day window cleans up.
        builder.Entity<RuntimeGitConfig>(e =>
        {
            e.HasKey(x => x.Id);

            e.HasIndex(x => x.RuntimeId).IsUnique();

            e.Property(x => x.AutoCommit).HasDefaultValue(true);
            e.Property(x => x.DeployKey).HasColumnType("text");
            e.Property(x => x.DeployKeyHostKey).HasColumnType("text");

            e.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
            e.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");
            e.Property(x => x.DeletedAt).HasColumnType("timestamp with time zone");

            e.HasOne<Source.Features.RuntimeLifecycle.Models.ProjectRuntime>()
                .WithMany()
                .HasForeignKey(x => x.RuntimeId)
                .OnDelete(DeleteBehavior.NoAction);

            e.HasQueryFilter(x => !x.IsDeleted);
        });

        // -------- ProjectSecrets (ProjectSecret) --------
        // One encrypted env-var row per (project, key). Plaintext never lands
        // here — only ciphertext + nonce + DEK version. ProjectId is a plain
        // Guid (no FK) because the Project entity is owned by another slice
        // and the audit-coherence story requires the row to outlive a project
        // hard-delete, mirroring the ProjectRuntime.ProjectId convention.
        // Soft-deletable so SecretAuditEvent.SecretId references stay coherent;
        // the global query filter hides deleted rows from default queries.
        // Unique partial index on (ProjectId, Key) where DeletedAt IS NULL —
        // enforces "one live row per (project, key)" while letting deleted rows
        // co-exist for audit. Done via HasFilter so it lands at the Postgres
        // level (EF Core's default unique index would conflict with re-creating
        // a key the operator previously deleted).
        builder.Entity<ProjectSecret>(e =>
        {
            e.HasKey(x => x.Id);

            e.Property(x => x.Key).HasMaxLength(200).IsRequired();
            e.Property(x => x.Ciphertext).IsRequired();
            e.Property(x => x.Nonce).IsRequired();
            e.Property(x => x.DekVersion).HasDefaultValue(1);
            e.Property(x => x.Version).HasDefaultValue(1);
            e.Property(x => x.IsSecret).HasDefaultValue(true);
            e.Property(x => x.CreatedBy).HasMaxLength(450);

            e.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
            e.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");
            e.Property(x => x.DeletedAt).HasColumnType("timestamp with time zone");

            // Unique partial index — "one live row per (project, branch, key)".
            // Deleted rows are kept for audit and ignored by the constraint so the
            // operator can re-add a deleted key without conflict.
            //
            // BranchId is nullable: null = project-wide default, non-null =
            // branch-specific override. Postgres treats NULLs as DISTINCT in a
            // unique index by default, so two project-wide rows (BranchId=null)
            // with the same key would NOT collide. We disable that with
            // .AreNullsDistinct(false) (EF Core 9 → Postgres "NULLS NOT
            // DISTINCT"), so a duplicate project-wide key is rejected just like a
            // duplicate branch-scoped key.
            e.HasIndex(x => new { x.ProjectId, x.BranchId, x.Key })
                .IsUnique()
                .AreNullsDistinct(false)
                .HasFilter("\"DeletedAt\" IS NULL")
                .HasDatabaseName("IX_ProjectSecrets_ProjectId_BranchId_Key");

            // Optional FK to User on CreatedBy — Restrict so deleting a user
            // doesn't cascade-nuke the audit-bearing secret rows.
            e.HasOne<User>()
                .WithMany()
                .HasForeignKey(x => x.CreatedBy)
                .OnDelete(DeleteBehavior.Restrict);

            // FK to ProjectBranch on BranchId — nullable, Restrict on delete.
            // Matches the ProjectRuntime.BranchId / Conversation.BranchId
            // convention: branches aren't deleted in v1, and Restrict surfaces a
            // future "delete a branch with scoped secrets" attempt rather than
            // silently orphaning or cascade-deleting env vars (silent data loss).
            e.HasOne<ProjectBranch>()
                .WithMany()
                .HasForeignKey(x => x.BranchId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasQueryFilter(x => !x.IsDeleted);
        });

        // -------- ProjectSecrets (ProjectKeyMaterial) --------
        // One row per project (enforced by a unique index on ProjectId) carrying
        // the wrapped DEK + master-key version. Lazily created on first secret
        // write (Card 2 handles that). Plain-Guid ProjectId mirrors ProjectSecret.
        // Soft-deletable for the same audit-coherence reason.
        builder.Entity<ProjectKeyMaterial>(e =>
        {
            e.HasKey(x => x.Id);

            e.HasIndex(x => x.ProjectId).IsUnique();

            e.Property(x => x.WrappedDek).IsRequired();
            e.Property(x => x.MasterKeyVersion).HasDefaultValue(1);

            e.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
            e.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");
            e.Property(x => x.DeletedAt).HasColumnType("timestamp with time zone");

            e.HasQueryFilter(x => !x.IsDeleted);
        });

        builder.Entity<WorkspaceKeyMaterial>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.WorkspaceId).IsUnique();
            e.Property(x => x.WrappedDek).IsRequired();
            e.Property(x => x.MasterKeyVersion).HasDefaultValue(1);
            e.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
            e.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");
            e.Property(x => x.DeletedAt).HasColumnType("timestamp with time zone");
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        // -------- ProjectSecrets (SecretAuditEvent) --------
        // Append-only audit row, one per action against the secrets subsystem.
        // NOT IAuditable and NOT ISoftDelete — only CreatedAt is a domain
        // timestamp; we never want a global interceptor stamping audit rows.
        // No FK on ProjectId / SecretId: the audit trail must outlive any of
        // those rows being hard-deleted, mirroring RuntimeTokenIssue /
        // RuntimeStateEvent. Composite index on (ProjectId, CreatedAt DESC)
        // — DESC is applied via raw SQL in the migration since EF Core 9
        // doesn't expose per-column sort order on relational indexes.
        builder.Entity<SecretAuditEvent>(e =>
        {
            e.HasKey(x => x.Id);

            e.Property(x => x.Action).HasConversion<int>();
            e.Property(x => x.SecretKey).HasMaxLength(200);
            e.Property(x => x.Actor).HasMaxLength(450).IsRequired();
            e.Property(x => x.Metadata).HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");

            // "Audit trail for project X, latest first" — dominant read pattern.
            // DESC on CreatedAt is applied via raw SQL in the migration.
            e.HasIndex(x => new { x.ProjectId, x.CreatedAt })
                .HasDatabaseName("IX_SecretAuditEvents_ProjectId_CreatedAt");
        });

        // -------- Mcp (McpServer) --------
        // Catalog row per registered MCP. Soft-deletable so future per-project
        // enablement rows referencing the server stay coherent across a yank-
        // then-re-register cycle. Unique partial index on Name where
        // DeletedAt IS NULL — mirrors the ProjectSecret (ProjectId, Key)
        // pattern so an operator can re-register a previously deleted MCP
        // without conflict. BaseUrl is intentionally NOT stored — derived at
        // runtime from the request host.
        builder.Entity<McpServer>(e =>
        {
            e.HasKey(x => x.Id);

            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Version).HasMaxLength(20).IsRequired();
            e.Property(x => x.DefaultEnabled).HasDefaultValue(true);

            e.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
            e.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");
            e.Property(x => x.DeletedAt).HasColumnType("timestamp with time zone");

            // Unique partial index — "one live row per Name". Deleted rows are
            // kept and ignored by the constraint so the operator can re-add a
            // deleted MCP without conflict. Matches ProjectSecret pattern.
            e.HasIndex(x => x.Name)
                .IsUnique()
                .HasFilter("\"DeletedAt\" IS NULL")
                .HasDatabaseName("IX_McpServers_Name");

            e.HasQueryFilter(x => !x.IsDeleted);
        });

        // -------- Mcp (McpCall) --------
        // Append-only audit row, one per MCP call. NOT IAuditable and NOT
        // ISoftDelete — only CreatedAt is a domain timestamp; we never want a
        // global interceptor stamping audit rows. No FK on RuntimeId or to
        // McpServer: the audit trail must outlive any of those rows being hard-
        // deleted, mirroring SecretAuditEvent / RuntimeTokenIssue / RuntimeStateEvent.
        // ServerName is denormalized for the same outlive-the-row reason.
        // Composite indexes (RuntimeId, CreatedAt DESC) and
        // (ServerName, Method, CreatedAt DESC) — DESC variants are emitted via
        // raw SQL in the migration since EF Core 9 doesn't expose per-column
        // sort order on relational indexes. The fluent indexes here are
        // placeholders so EF tracks the columns; the migration drops them and
        // re-emits the DESC variants.
        // **NEVER store request / response bodies — only sizes**, by design.
        builder.Entity<McpCall>(e =>
        {
            e.HasKey(x => x.Id);

            e.Property(x => x.ServerName).HasMaxLength(100).IsRequired();
            e.Property(x => x.Method).HasMaxLength(100).IsRequired();
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.ErrorCode).HasMaxLength(100);
            e.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");

            // "Latest calls for this runtime" — dominant read pattern.
            // DESC on CreatedAt is applied via raw SQL in the migration.
            e.HasIndex(x => new { x.RuntimeId, x.CreatedAt })
                .HasDatabaseName("IX_McpCalls_RuntimeId_CreatedAt");

            // "Latest calls for this (server, method)" — dashboard / abuse
            // forensics read pattern. DESC on CreatedAt is applied via raw SQL.
            e.HasIndex(x => new { x.ServerName, x.Method, x.CreatedAt })
                .HasDatabaseName("IX_McpCalls_ServerName_Method_CreatedAt");
        });

        // -------- ProjectKanban (ProjectKanbanCard) --------
        // One card on a project's kanban board. Plain-Guid ProjectId mirrors
        // ProjectSecret / ProjectRuntime — Project entity lives in another
        // slice and the card row must outlive a project hard-delete. Soft-
        // deletable so removed cards stay queryable for audit. Composite index
        // (ProjectId, Status, Position) matches the dominant read pattern
        // "list cards in column X of project Y in display order" — non-DESC
        // so EF emits it normally, no raw SQL needed.
        builder.Entity<ProjectKanbanCard>(e =>
        {
            e.HasKey(x => x.Id);

            e.Property(x => x.Title).HasMaxLength(500).IsRequired();
            e.Property(x => x.Description).HasColumnType("text");
            e.Property(x => x.Status).HasConversion<int>();
            // Card 2: priority + due date columns. Priority stored as int so
            // future enum entries don't migrate existing rows; DueDate is a
            // nullable timestamptz mirroring the audit columns.
            e.Property(x => x.Priority).HasConversion<int>();
            e.Property(x => x.DueDate).HasColumnType("timestamp with time zone");
            e.Property(x => x.CreatedBy).HasMaxLength(450);

            // kanban-card-provenance spec: who opened the row, and which git
            // branch was active in the daemon's workspace at the moment of
            // creation. Source persists as int (same convention as Status /
            // Priority) so future enum members don't rewrite existing rows.
            // CreatedOnBranch is plain text — branch names are unbounded by
            // git (refspecs support /-separated paths), no maxlength here.
            e.Property(x => x.Source).HasConversion<int>();
            e.Property(x => x.CreatedOnBranch).HasColumnType("text");

            e.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
            e.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");
            e.Property(x => x.DeletedAt).HasColumnType("timestamp with time zone");

            // "Cards in column X of project Y in display order" — dominant
            // read pattern. Non-DESC so EF emits it normally.
            e.HasIndex(x => new { x.ProjectId, x.Status, x.Position })
                .HasDatabaseName("IX_ProjectKanbanCards_ProjectId_Status_Position");

            // Optional FK to User on CreatedBy — Restrict so deleting a user
            // doesn't cascade-nuke their kanban cards. Mirrors ProjectSecret.
            e.HasOne<User>()
                .WithMany()
                .HasForeignKey(x => x.CreatedBy)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasQueryFilter(x => !x.IsDeleted);
        });

        // -------- ProjectKanban (ProjectKanbanCardSubtask) --------
        // Card 2: checklist items nested inside a ProjectKanbanCard. Real FK
        // to the parent card with OnDelete=Cascade — if the parent is ever
        // hard-deleted (dev tool, not a runtime path) the children go with
        // it. The parent normally soft-deletes; both rows stay queryable via
        // the global filter in that case.
        builder.Entity<ProjectKanbanCardSubtask>(e =>
        {
            e.HasKey(x => x.Id);

            e.Property(x => x.Title).HasMaxLength(500).IsRequired();

            e.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
            e.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");
            e.Property(x => x.DeletedAt).HasColumnType("timestamp with time zone");

            // "Render this card's checklist in order" — dominant read pattern.
            e.HasIndex(x => new { x.ProjectKanbanCardId, x.Position })
                .HasDatabaseName("IX_ProjectKanbanCardSubtasks_CardId_Position");

            // FK to parent card. Cascade keeps the children consistent with
            // the parent's hard-delete; soft-delete uses the global filter
            // instead.
            e.HasOne<ProjectKanbanCard>()
                .WithMany()
                .HasForeignKey(x => x.ProjectKanbanCardId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasQueryFilter(x => !x.IsDeleted);
        });

        // -------- Specifications (Specification) --------
        // One product spec for a project. Plain-Guid ProjectId mirrors
        // ProjectKanbanCard / ProjectSecret — Project lives in another slice
        // and the spec row must outlive a project hard-delete. Soft-deletable;
        // the unique (ProjectId, Slug) index is filtered to !IsDeleted so a
        // deleted slug can be re-used (a fresh row is minted; the deleted row
        // stays for audit).
        builder.Entity<Specification>(e =>
        {
            e.HasKey(x => x.Id);

            e.Property(x => x.Slug).HasMaxLength(200).IsRequired();
            e.Property(x => x.Name).HasMaxLength(500).IsRequired();
            e.Property(x => x.Content).HasColumnType("text").IsRequired();
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.CreatedBy).HasMaxLength(450);

            e.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
            e.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");
            e.Property(x => x.DeletedAt).HasColumnType("timestamp with time zone");

            // Unique slug per project — filtered to !IsDeleted so re-creating
            // a previously-deleted slug is permitted. The filtered unique index
            // is the only thing the upsert handler relies on for "slug
            // available?"; the global query filter on the entity does the
            // rest.
            e.HasIndex(x => new { x.ProjectId, x.Slug })
                .IsUnique()
                .HasFilter("\"IsDeleted\" = false")
                .HasDatabaseName("IX_Specifications_ProjectId_Slug");

            // Optional FK to User on CreatedBy — Restrict so deleting a user
            // doesn't cascade-nuke their specs. Mirrors ProjectKanbanCard.
            e.HasOne<User>()
                .WithMany()
                .HasForeignKey(x => x.CreatedBy)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasQueryFilter(x => !x.IsDeleted);
        });

        // -------- Cloudflare (SubdomainAssignment) --------
        // One row per pre-provisioned preview subdomain in the pool. Lifecycle
        // is Available -> Assigned -> Releasing -> destroyed (the row is hard-
        // deleted; never reused). Status stored as a string so the audit /
        // telemetry trail is human-readable. Soft-delete filter mirrors the
        // rest of the platform.
        //
        // Phase 3 wires the FK from AssignedBranchId -> ProjectBranches with
        // OnDelete=SetNull: hard-deleting a branch nulls the FK on the
        // (rare) surviving subdomain row rather than cascading the pool row
        // away. Released rows are preserved for the destroy-and-never-reuse
        // audit trail. One-to-one cardinality is the natural shape — a branch
        // claims exactly one subdomain.
        builder.Entity<SubdomainAssignment>(e =>
        {
            e.HasKey(x => x.Id);

            e.Property(x => x.Hostname).IsRequired().HasMaxLength(255);
            e.Property(x => x.Subdomain).IsRequired().HasMaxLength(64);
            e.Property(x => x.TunnelId).IsRequired().HasMaxLength(64);
            e.Property(x => x.TunnelToken).IsRequired().HasColumnType("text");
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

            e.Property(x => x.AssignedAt).HasColumnType("timestamp with time zone");
            e.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
            e.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");
            e.Property(x => x.DeletedAt).HasColumnType("timestamp with time zone");

            // Globally unique hostname — DB-level safety net on top of the
            // app-level collision check in BatchCreateSubdomainsHandler.
            e.HasIndex(x => x.Hostname)
                .IsUnique()
                .HasDatabaseName("IX_SubdomainAssignments_Hostname");

            // Pool query: "next Available row" — and the dashboard read
            // pattern "count by status". Indexed for both.
            e.HasIndex(x => x.Status)
                .HasDatabaseName("IX_SubdomainAssignments_Status");

            // FK to ProjectBranch with SetNull on delete (Phase 3). Wired as a
            // 1:0..1 — one subdomain at most per branch, one branch at most per
            // subdomain. Unique-when-not-null index on AssignedBranchId
            // enforces the "at most one subdomain per branch" half of the
            // invariant at the DB level; the application-level FOR UPDATE
            // SKIP LOCKED claim in AssignSubdomainToBranchHandler enforces the
            // other half (one row claimed atomically per call).
            e.HasOne<ProjectBranch>()
                .WithOne(b => b.AssignedSubdomain)
                .HasForeignKey<SubdomainAssignment>(x => x.AssignedBranchId)
                .OnDelete(DeleteBehavior.SetNull);

            // Partial unique index: only enforce "one subdomain per branch"
            // when AssignedBranchId is non-null. Available pool rows all have
            // null here and would otherwise collide on the uniqueness
            // constraint.
            e.HasIndex(x => x.AssignedBranchId)
                .IsUnique()
                .HasFilter("\"AssignedBranchId\" IS NOT NULL")
                .HasDatabaseName("IX_SubdomainAssignments_AssignedBranchId");

            // Soft-delete filter so default queries skip tombstoned rows.
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        // -------- CursorModels --------
        // Sibling catalogue to OpencodeModels / AgentModels, but for the Cursor
        // SDK (@cursor/sdk) backend. Same shape and ownership as OpencodeModels.
        // Mirrors all the same patterns: partial unique slug index, active-
        // listing index, soft-delete query filter, deterministic seed with
        // fixed Guids + timestamps.
        builder.Entity<CursorModel>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Slug).IsRequired().HasMaxLength(100);
            entity.Property(e => e.DisplayName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.IsActive).IsRequired().HasDefaultValue(true);

            entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");
            entity.Property(e => e.DeletedAt).HasColumnType("timestamp with time zone");

            // jsonb columns for the @cursor/sdk variant / parameter metadata.
            // We serialise the list values to JSON strings via HasConversion
            // (rather than relying on Npgsql's dynamic JSON serialiser) for
            // two reasons:
            //
            //   1. The migration scaffolder (CSharpHelper.UnknownLiteral) can
            //      emit string literals for HasData but cannot scaffold custom
            //      POCO types like CursorModelParameter / CursorModelVariant.
            //      Storing as a string sidesteps that limitation.
            //   2. Stable wire shape — CamelCase JSON keys, predictable
            //      ordering, no surprise round-tripping through the dynamic
            //      serialiser. The bytes on disk match the @cursor/sdk's own
            //      output, which is what the frontend already speaks.
            //
            // The HasColumnType("jsonb") keeps the storage type postgres-native
            // (queryable with `->>` / `@>` if a later card needs it), and the
            // ValueComparer ensures the EF change tracker detects mutations
            // inside the lists (which the default reference comparer would
            // miss).
            entity.Property(e => e.Aliases)
                .IsRequired()
                .HasColumnType("jsonb")
                .HasDefaultValueSql("'[]'::jsonb")
                .HasConversion(
                    v => JsonSerializer.Serialize(v, CursorModelJsonOptions),
                    v => JsonSerializer.Deserialize<List<string>>(v, CursorModelJsonOptions) ?? new List<string>(),
                    new ValueComparer<List<string>>(
                        (a, b) => (a ?? new List<string>()).SequenceEqual(b ?? new List<string>()),
                        v => v == null ? 0 : v.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode())),
                        v => v == null ? new List<string>() : v.ToList()));
            entity.Property(e => e.Parameters)
                .IsRequired()
                .HasColumnType("jsonb")
                .HasDefaultValueSql("'[]'::jsonb")
                .HasConversion(
                    v => JsonSerializer.Serialize(v, CursorModelJsonOptions),
                    v => JsonSerializer.Deserialize<List<CursorModelParameter>>(v, CursorModelJsonOptions) ?? new List<CursorModelParameter>(),
                    new ValueComparer<List<CursorModelParameter>>(
                        (a, b) => JsonSerializer.Serialize(a, CursorModelJsonOptions) == JsonSerializer.Serialize(b, CursorModelJsonOptions),
                        v => v == null ? 0 : JsonSerializer.Serialize(v, CursorModelJsonOptions).GetHashCode(),
                        v => v == null ? new List<CursorModelParameter>() : JsonSerializer.Deserialize<List<CursorModelParameter>>(JsonSerializer.Serialize(v, CursorModelJsonOptions), CursorModelJsonOptions) ?? new List<CursorModelParameter>()));
            entity.Property(e => e.Variants)
                .IsRequired()
                .HasColumnType("jsonb")
                .HasDefaultValueSql("'[]'::jsonb")
                .HasConversion(
                    v => JsonSerializer.Serialize(v, CursorModelJsonOptions),
                    v => JsonSerializer.Deserialize<List<CursorModelVariant>>(v, CursorModelJsonOptions) ?? new List<CursorModelVariant>(),
                    new ValueComparer<List<CursorModelVariant>>(
                        (a, b) => JsonSerializer.Serialize(a, CursorModelJsonOptions) == JsonSerializer.Serialize(b, CursorModelJsonOptions),
                        v => v == null ? 0 : JsonSerializer.Serialize(v, CursorModelJsonOptions).GetHashCode(),
                        v => v == null ? new List<CursorModelVariant>() : JsonSerializer.Deserialize<List<CursorModelVariant>>(JsonSerializer.Serialize(v, CursorModelJsonOptions), CursorModelJsonOptions) ?? new List<CursorModelVariant>()));

            // Picker order — lower SortOrder comes first. Seed uses the SDK's
            // natural order (default=0, composer-2.5=1, …) so the picker
            // mirrors what `cursor models list` shows.
            entity.Property(e => e.SortOrder).IsRequired().HasDefaultValue(0);

            // Partial unique index — Slug is unique among non-tombstoned rows.
            // Lets an admin recreate a slug after delete without colliding with
            // the soft-deleted historical row. Same pattern as OpencodeModels.
            entity.HasIndex(e => e.Slug)
                .IsUnique()
                .HasFilter("\"IsDeleted\" = false")
                .HasDatabaseName("IX_CursorModels_Slug");

            // Catalogue listing order — the picker query filters IsActive and
            // sorts by SortOrder. Indexed for the "active models" lookup the
            // project settings UI runs on every settings page load.
            entity.HasIndex(e => e.IsActive)
                .HasDatabaseName("IX_CursorModels_IsActive");

            // Soft-delete query filter — admin paths use IgnoreQueryFilters().
            entity.HasQueryFilter(e => !e.IsDeleted);

            // Deterministic seed. Fixed Guids + fixed timestamps keep HasData
            // re-runs (e.g. `dotnet ef migrations add` regenerations) stable.
            // The first two rows preserve their existing Guids
            // (b0000000-…-0001 = composer-2, …-0002 = gpt-5.5) because
            // Projects.CursorModelId / AgentSessions.CursorModelId reference
            // them; all 26 new rows take Guids …-0003 through …-001c. SortOrder
            // mirrors the SDK's natural `Cursor.models.list()` order, so
            // `default` (Auto) appears first in the picker even though it sits
            // at Guid …-0003 in the table.
            var cursorSeedTimestamp = new DateTime(2026, 5, 23, 0, 0, 0, DateTimeKind.Utc);
            entity.HasData(
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-000000000001"),
                    Slug = "composer-2",
                    DisplayName = "Composer 2",
                    Description = (string?)null,
                    IsActive = true,
                    Aliases = new List<string>(),
                    Parameters = new List<CursorModelParameter> { new CursorModelParameter { Id = "fast", DisplayName = "Fast", Values = new List<CursorModelParameterValue> { new CursorModelParameterValue { Value = "false", DisplayName = null }, new CursorModelParameterValue { Value = "true", DisplayName = "Fast" } } } },
                    Variants = new List<CursorModelVariant> { new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Composer 2", IsDefault = true }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Composer 2", IsDefault = false } },
                    SortOrder = 2,
                    CreatedAt = cursorSeedTimestamp,
                    UpdatedAt = cursorSeedTimestamp,
                    IsDeleted = false,
                },
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-000000000002"),
                    Slug = "gpt-5.5",
                    DisplayName = "GPT-5.5",
                    Description = (string?)null,
                    IsActive = true,
                    Aliases = new List<string> { "gpt-latest", "gpt", "gpt-5-5" },
                    Parameters = new List<CursorModelParameter> { new CursorModelParameter { Id = "context", DisplayName = "Context", Values = new List<CursorModelParameterValue> { new CursorModelParameterValue { Value = "272k", DisplayName = "272K" }, new CursorModelParameterValue { Value = "1m", DisplayName = "1M" } } }, new CursorModelParameter { Id = "reasoning", DisplayName = "Reasoning", Values = new List<CursorModelParameterValue> { new CursorModelParameterValue { Value = "none", DisplayName = "None" }, new CursorModelParameterValue { Value = "low", DisplayName = "Low" }, new CursorModelParameterValue { Value = "medium", DisplayName = "Medium" }, new CursorModelParameterValue { Value = "high", DisplayName = "High" }, new CursorModelParameterValue { Value = "extra-high", DisplayName = "Extra High" } } }, new CursorModelParameter { Id = "fast", DisplayName = "Fast", Values = new List<CursorModelParameterValue> { new CursorModelParameterValue { Value = "false", DisplayName = null }, new CursorModelParameterValue { Value = "true", DisplayName = "Fast" } } } },
                    Variants = new List<CursorModelVariant> { new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "context", Value = "272k" }, new CursorModelVariantParam { Id = "reasoning", Value = "none" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "GPT-5.5", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "context", Value = "272k" }, new CursorModelVariantParam { Id = "reasoning", Value = "none" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "GPT-5.5", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "context", Value = "272k" }, new CursorModelVariantParam { Id = "reasoning", Value = "low" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "GPT-5.5", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "context", Value = "272k" }, new CursorModelVariantParam { Id = "reasoning", Value = "low" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "GPT-5.5", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "context", Value = "272k" }, new CursorModelVariantParam { Id = "reasoning", Value = "medium" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "GPT-5.5", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "context", Value = "272k" }, new CursorModelVariantParam { Id = "reasoning", Value = "medium" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "GPT-5.5", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "context", Value = "272k" }, new CursorModelVariantParam { Id = "reasoning", Value = "high" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "GPT-5.5", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "context", Value = "272k" }, new CursorModelVariantParam { Id = "reasoning", Value = "high" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "GPT-5.5", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "context", Value = "272k" }, new CursorModelVariantParam { Id = "reasoning", Value = "extra-high" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "GPT-5.5", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "context", Value = "272k" }, new CursorModelVariantParam { Id = "reasoning", Value = "extra-high" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "GPT-5.5", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "reasoning", Value = "none" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "GPT-5.5", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "reasoning", Value = "low" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "GPT-5.5", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "reasoning", Value = "medium" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "GPT-5.5", IsDefault = true }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "reasoning", Value = "high" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "GPT-5.5", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "reasoning", Value = "extra-high" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "GPT-5.5", IsDefault = false } },
                    SortOrder = 3,
                    CreatedAt = cursorSeedTimestamp,
                    UpdatedAt = cursorSeedTimestamp,
                    IsDeleted = false,
                },
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-000000000003"),
                    Slug = "default",
                    DisplayName = "Auto",
                    Description = (string?)null,
                    IsActive = true,
                    Aliases = new List<string> { "auto" },
                    Parameters = new List<CursorModelParameter>(),
                    Variants = new List<CursorModelVariant> { new CursorModelVariant { Params = new List<CursorModelVariantParam> {  }, DisplayName = "Auto", IsDefault = true } },
                    SortOrder = 0,
                    CreatedAt = cursorSeedTimestamp,
                    UpdatedAt = cursorSeedTimestamp,
                    IsDeleted = false,
                },
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-000000000004"),
                    Slug = "composer-2.5",
                    DisplayName = "Composer 2.5",
                    Description = (string?)null,
                    IsActive = true,
                    Aliases = new List<string> { "composer-latest", "composer", "composer-2-5" },
                    Parameters = new List<CursorModelParameter> { new CursorModelParameter { Id = "fast", DisplayName = "Fast", Values = new List<CursorModelParameterValue> { new CursorModelParameterValue { Value = "false", DisplayName = null }, new CursorModelParameterValue { Value = "true", DisplayName = "Fast" } } } },
                    Variants = new List<CursorModelVariant> { new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Composer 2.5", IsDefault = true }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Composer 2.5", IsDefault = false } },
                    SortOrder = 1,
                    CreatedAt = cursorSeedTimestamp,
                    UpdatedAt = cursorSeedTimestamp,
                    IsDeleted = false,
                },
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-000000000005"),
                    Slug = "gpt-5.3-codex",
                    DisplayName = "Codex 5.3",
                    Description = (string?)null,
                    IsActive = true,
                    Aliases = new List<string> { "codex-latest", "codex", "codex-5.3" },
                    Parameters = new List<CursorModelParameter> { new CursorModelParameter { Id = "reasoning", DisplayName = "Reasoning", Values = new List<CursorModelParameterValue> { new CursorModelParameterValue { Value = "low", DisplayName = "Low" }, new CursorModelParameterValue { Value = "medium", DisplayName = "Medium" }, new CursorModelParameterValue { Value = "high", DisplayName = "High" }, new CursorModelParameterValue { Value = "extra-high", DisplayName = "Extra High" } } }, new CursorModelParameter { Id = "fast", DisplayName = "Fast", Values = new List<CursorModelParameterValue> { new CursorModelParameterValue { Value = "false", DisplayName = null }, new CursorModelParameterValue { Value = "true", DisplayName = "Fast" } } } },
                    Variants = new List<CursorModelVariant> { new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "low" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Codex 5.3", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "low" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Codex 5.3", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "medium" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Codex 5.3", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "medium" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Codex 5.3", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "high" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Codex 5.3", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "high" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Codex 5.3", IsDefault = true }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "extra-high" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Codex 5.3", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "extra-high" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Codex 5.3", IsDefault = false } },
                    SortOrder = 4,
                    CreatedAt = cursorSeedTimestamp,
                    UpdatedAt = cursorSeedTimestamp,
                    IsDeleted = false,
                },
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-000000000006"),
                    Slug = "claude-sonnet-4-6",
                    DisplayName = "Sonnet 4.6",
                    Description = (string?)null,
                    IsActive = true,
                    Aliases = new List<string> { "sonnet-latest", "sonnet", "sonnet-4.6", "sonnet-4-6" },
                    Parameters = new List<CursorModelParameter> { new CursorModelParameter { Id = "thinking", DisplayName = "Thinking", Values = new List<CursorModelParameterValue> { new CursorModelParameterValue { Value = "false", DisplayName = null }, new CursorModelParameterValue { Value = "true", DisplayName = ":icon-brain:" } } }, new CursorModelParameter { Id = "context", DisplayName = "Context", Values = new List<CursorModelParameterValue> { new CursorModelParameterValue { Value = "200k", DisplayName = "200K" }, new CursorModelParameterValue { Value = "1m", DisplayName = "1M" } } }, new CursorModelParameter { Id = "effort", DisplayName = "Effort", Values = new List<CursorModelParameterValue> { new CursorModelParameterValue { Value = "low", DisplayName = "Low" }, new CursorModelParameterValue { Value = "medium", DisplayName = "Medium" }, new CursorModelParameterValue { Value = "high", DisplayName = "High" }, new CursorModelParameterValue { Value = "max", DisplayName = "Max" } } } },
                    Variants = new List<CursorModelVariant> { new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "200k" }, new CursorModelVariantParam { Id = "effort", Value = "low" } }, DisplayName = "Sonnet 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "200k" }, new CursorModelVariantParam { Id = "effort", Value = "medium" } }, DisplayName = "Sonnet 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "200k" }, new CursorModelVariantParam { Id = "effort", Value = "high" } }, DisplayName = "Sonnet 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "200k" }, new CursorModelVariantParam { Id = "effort", Value = "max" } }, DisplayName = "Sonnet 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "low" } }, DisplayName = "Sonnet 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "medium" } }, DisplayName = "Sonnet 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "high" } }, DisplayName = "Sonnet 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "max" } }, DisplayName = "Sonnet 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "200k" }, new CursorModelVariantParam { Id = "effort", Value = "low" } }, DisplayName = "Sonnet 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "200k" }, new CursorModelVariantParam { Id = "effort", Value = "medium" } }, DisplayName = "Sonnet 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "200k" }, new CursorModelVariantParam { Id = "effort", Value = "high" } }, DisplayName = "Sonnet 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "200k" }, new CursorModelVariantParam { Id = "effort", Value = "max" } }, DisplayName = "Sonnet 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "low" } }, DisplayName = "Sonnet 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "medium" } }, DisplayName = "Sonnet 4.6", IsDefault = true }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "high" } }, DisplayName = "Sonnet 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "max" } }, DisplayName = "Sonnet 4.6", IsDefault = false } },
                    SortOrder = 5,
                    CreatedAt = cursorSeedTimestamp,
                    UpdatedAt = cursorSeedTimestamp,
                    IsDeleted = false,
                },
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-000000000007"),
                    Slug = "claude-opus-4-7",
                    DisplayName = "Opus 4.7",
                    Description = (string?)null,
                    IsActive = true,
                    Aliases = new List<string> { "opus-latest", "opus", "opus-4.7", "opus-4-7" },
                    Parameters = new List<CursorModelParameter> { new CursorModelParameter { Id = "thinking", DisplayName = "Thinking", Values = new List<CursorModelParameterValue> { new CursorModelParameterValue { Value = "false", DisplayName = null }, new CursorModelParameterValue { Value = "true", DisplayName = ":icon-brain:" } } }, new CursorModelParameter { Id = "context", DisplayName = "Context", Values = new List<CursorModelParameterValue> { new CursorModelParameterValue { Value = "300k", DisplayName = "300K" }, new CursorModelParameterValue { Value = "1m", DisplayName = "1M" } } }, new CursorModelParameter { Id = "effort", DisplayName = "Effort", Values = new List<CursorModelParameterValue> { new CursorModelParameterValue { Value = "low", DisplayName = "Low" }, new CursorModelParameterValue { Value = "medium", DisplayName = "Medium" }, new CursorModelParameterValue { Value = "high", DisplayName = "High" }, new CursorModelParameterValue { Value = "xhigh", DisplayName = "Extra High" }, new CursorModelParameterValue { Value = "max", DisplayName = "Max" } } }, new CursorModelParameter { Id = "fast", DisplayName = "Fast", Values = new List<CursorModelParameterValue> { new CursorModelParameterValue { Value = "false", DisplayName = null }, new CursorModelParameterValue { Value = "true", DisplayName = "Fast" } } } },
                    Variants = new List<CursorModelVariant> { new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "300k" }, new CursorModelVariantParam { Id = "effort", Value = "low" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Opus 4.7", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "300k" }, new CursorModelVariantParam { Id = "effort", Value = "low" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Opus 4.7", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "300k" }, new CursorModelVariantParam { Id = "effort", Value = "medium" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Opus 4.7", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "300k" }, new CursorModelVariantParam { Id = "effort", Value = "medium" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Opus 4.7", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "300k" }, new CursorModelVariantParam { Id = "effort", Value = "high" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Opus 4.7", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "300k" }, new CursorModelVariantParam { Id = "effort", Value = "high" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Opus 4.7", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "300k" }, new CursorModelVariantParam { Id = "effort", Value = "xhigh" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Opus 4.7", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "300k" }, new CursorModelVariantParam { Id = "effort", Value = "xhigh" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Opus 4.7", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "300k" }, new CursorModelVariantParam { Id = "effort", Value = "max" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Opus 4.7", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "300k" }, new CursorModelVariantParam { Id = "effort", Value = "max" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Opus 4.7", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "low" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Opus 4.7", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "low" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Opus 4.7", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "medium" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Opus 4.7", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "medium" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Opus 4.7", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "high" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Opus 4.7", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "high" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Opus 4.7", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "xhigh" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Opus 4.7", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "xhigh" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Opus 4.7", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "max" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Opus 4.7", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "max" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Opus 4.7", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "300k" }, new CursorModelVariantParam { Id = "effort", Value = "low" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Opus 4.7", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "300k" }, new CursorModelVariantParam { Id = "effort", Value = "low" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Opus 4.7", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "300k" }, new CursorModelVariantParam { Id = "effort", Value = "medium" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Opus 4.7", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "300k" }, new CursorModelVariantParam { Id = "effort", Value = "medium" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Opus 4.7", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "300k" }, new CursorModelVariantParam { Id = "effort", Value = "high" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Opus 4.7", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "300k" }, new CursorModelVariantParam { Id = "effort", Value = "high" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Opus 4.7", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "300k" }, new CursorModelVariantParam { Id = "effort", Value = "xhigh" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Opus 4.7", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "300k" }, new CursorModelVariantParam { Id = "effort", Value = "xhigh" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Opus 4.7", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "300k" }, new CursorModelVariantParam { Id = "effort", Value = "max" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Opus 4.7", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "300k" }, new CursorModelVariantParam { Id = "effort", Value = "max" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Opus 4.7", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "low" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Opus 4.7", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "low" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Opus 4.7", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "medium" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Opus 4.7", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "medium" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Opus 4.7", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "high" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Opus 4.7", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "high" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Opus 4.7", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "xhigh" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Opus 4.7", IsDefault = true }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "xhigh" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Opus 4.7", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "max" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Opus 4.7", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "cyber", Value = "false" }, new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "max" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Opus 4.7", IsDefault = false } },
                    SortOrder = 6,
                    CreatedAt = cursorSeedTimestamp,
                    UpdatedAt = cursorSeedTimestamp,
                    IsDeleted = false,
                },
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-000000000008"),
                    Slug = "grok-build-0.1",
                    DisplayName = "Grok Build 0.1",
                    Description = (string?)null,
                    IsActive = true,
                    Aliases = new List<string> { "grok-build" },
                    Parameters = new List<CursorModelParameter> { new CursorModelParameter { Id = "context", DisplayName = "Context", Values = new List<CursorModelParameterValue> { new CursorModelParameterValue { Value = "200k", DisplayName = "200K" }, new CursorModelParameterValue { Value = "1m", DisplayName = "1M" } } } },
                    Variants = new List<CursorModelVariant> { new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "context", Value = "200k" } }, DisplayName = "Grok Build 0.1", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "context", Value = "1m" } }, DisplayName = "Grok Build 0.1", IsDefault = true } },
                    SortOrder = 7,
                    CreatedAt = cursorSeedTimestamp,
                    UpdatedAt = cursorSeedTimestamp,
                    IsDeleted = false,
                },
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-000000000009"),
                    Slug = "gpt-5.4",
                    DisplayName = "GPT-5.4",
                    Description = (string?)null,
                    IsActive = true,
                    Aliases = new List<string> { "gpt" },
                    Parameters = new List<CursorModelParameter> { new CursorModelParameter { Id = "context", DisplayName = "Context", Values = new List<CursorModelParameterValue> { new CursorModelParameterValue { Value = "272k", DisplayName = "272K" }, new CursorModelParameterValue { Value = "1m", DisplayName = "1M" } } }, new CursorModelParameter { Id = "reasoning", DisplayName = "Reasoning", Values = new List<CursorModelParameterValue> { new CursorModelParameterValue { Value = "none", DisplayName = "None" }, new CursorModelParameterValue { Value = "low", DisplayName = "Low" }, new CursorModelParameterValue { Value = "medium", DisplayName = "Medium" }, new CursorModelParameterValue { Value = "high", DisplayName = "High" }, new CursorModelParameterValue { Value = "extra-high", DisplayName = "Extra High" } } }, new CursorModelParameter { Id = "fast", DisplayName = "Fast", Values = new List<CursorModelParameterValue> { new CursorModelParameterValue { Value = "false", DisplayName = null }, new CursorModelParameterValue { Value = "true", DisplayName = "Fast" } } } },
                    Variants = new List<CursorModelVariant> { new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "context", Value = "272k" }, new CursorModelVariantParam { Id = "reasoning", Value = "none" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "GPT-5.4", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "context", Value = "272k" }, new CursorModelVariantParam { Id = "reasoning", Value = "none" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "GPT-5.4", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "context", Value = "272k" }, new CursorModelVariantParam { Id = "reasoning", Value = "low" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "GPT-5.4", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "context", Value = "272k" }, new CursorModelVariantParam { Id = "reasoning", Value = "low" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "GPT-5.4", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "context", Value = "272k" }, new CursorModelVariantParam { Id = "reasoning", Value = "medium" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "GPT-5.4", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "context", Value = "272k" }, new CursorModelVariantParam { Id = "reasoning", Value = "medium" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "GPT-5.4", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "context", Value = "272k" }, new CursorModelVariantParam { Id = "reasoning", Value = "high" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "GPT-5.4", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "context", Value = "272k" }, new CursorModelVariantParam { Id = "reasoning", Value = "high" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "GPT-5.4", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "context", Value = "272k" }, new CursorModelVariantParam { Id = "reasoning", Value = "extra-high" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "GPT-5.4", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "context", Value = "272k" }, new CursorModelVariantParam { Id = "reasoning", Value = "extra-high" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "GPT-5.4", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "reasoning", Value = "none" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "GPT-5.4", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "reasoning", Value = "low" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "GPT-5.4", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "reasoning", Value = "medium" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "GPT-5.4", IsDefault = true }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "reasoning", Value = "high" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "GPT-5.4", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "reasoning", Value = "extra-high" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "GPT-5.4", IsDefault = false } },
                    SortOrder = 8,
                    CreatedAt = cursorSeedTimestamp,
                    UpdatedAt = cursorSeedTimestamp,
                    IsDeleted = false,
                },
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-00000000000a"),
                    Slug = "claude-opus-4-6",
                    DisplayName = "Opus 4.6",
                    Description = (string?)null,
                    IsActive = true,
                    Aliases = new List<string> { "opus", "opus-4.6", "opus-4-6" },
                    Parameters = new List<CursorModelParameter> { new CursorModelParameter { Id = "thinking", DisplayName = "Thinking", Values = new List<CursorModelParameterValue> { new CursorModelParameterValue { Value = "false", DisplayName = null }, new CursorModelParameterValue { Value = "true", DisplayName = ":icon-brain:" } } }, new CursorModelParameter { Id = "context", DisplayName = "Context", Values = new List<CursorModelParameterValue> { new CursorModelParameterValue { Value = "200k", DisplayName = "200K" }, new CursorModelParameterValue { Value = "1m", DisplayName = "1M" } } }, new CursorModelParameter { Id = "effort", DisplayName = "Effort", Values = new List<CursorModelParameterValue> { new CursorModelParameterValue { Value = "low", DisplayName = "Low" }, new CursorModelParameterValue { Value = "medium", DisplayName = "Medium" }, new CursorModelParameterValue { Value = "high", DisplayName = "High" }, new CursorModelParameterValue { Value = "max", DisplayName = "Max" } } }, new CursorModelParameter { Id = "fast", DisplayName = "Fast", Values = new List<CursorModelParameterValue> { new CursorModelParameterValue { Value = "false", DisplayName = null }, new CursorModelParameterValue { Value = "true", DisplayName = "Fast" } } } },
                    Variants = new List<CursorModelVariant> { new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "200k" }, new CursorModelVariantParam { Id = "effort", Value = "low" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Opus 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "200k" }, new CursorModelVariantParam { Id = "effort", Value = "low" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Opus 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "200k" }, new CursorModelVariantParam { Id = "effort", Value = "medium" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Opus 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "200k" }, new CursorModelVariantParam { Id = "effort", Value = "medium" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Opus 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "200k" }, new CursorModelVariantParam { Id = "effort", Value = "high" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Opus 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "200k" }, new CursorModelVariantParam { Id = "effort", Value = "high" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Opus 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "200k" }, new CursorModelVariantParam { Id = "effort", Value = "max" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Opus 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "200k" }, new CursorModelVariantParam { Id = "effort", Value = "max" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Opus 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "low" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Opus 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "low" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Opus 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "medium" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Opus 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "medium" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Opus 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "high" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Opus 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "high" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Opus 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "max" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Opus 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "max" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Opus 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "200k" }, new CursorModelVariantParam { Id = "effort", Value = "low" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Opus 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "200k" }, new CursorModelVariantParam { Id = "effort", Value = "low" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Opus 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "200k" }, new CursorModelVariantParam { Id = "effort", Value = "medium" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Opus 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "200k" }, new CursorModelVariantParam { Id = "effort", Value = "medium" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Opus 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "200k" }, new CursorModelVariantParam { Id = "effort", Value = "high" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Opus 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "200k" }, new CursorModelVariantParam { Id = "effort", Value = "high" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Opus 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "200k" }, new CursorModelVariantParam { Id = "effort", Value = "max" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Opus 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "200k" }, new CursorModelVariantParam { Id = "effort", Value = "max" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Opus 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "low" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Opus 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "low" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Opus 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "medium" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Opus 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "medium" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Opus 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "high" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Opus 4.6", IsDefault = true }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "high" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Opus 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "max" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Opus 4.6", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "1m" }, new CursorModelVariantParam { Id = "effort", Value = "max" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Opus 4.6", IsDefault = false } },
                    SortOrder = 9,
                    CreatedAt = cursorSeedTimestamp,
                    UpdatedAt = cursorSeedTimestamp,
                    IsDeleted = false,
                },
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-00000000000b"),
                    Slug = "claude-opus-4-5",
                    DisplayName = "Opus 4.5",
                    Description = (string?)null,
                    IsActive = true,
                    Aliases = new List<string> { "opus", "opus-4.5", "opus-4-5" },
                    Parameters = new List<CursorModelParameter> { new CursorModelParameter { Id = "thinking", DisplayName = "Thinking", Values = new List<CursorModelParameterValue> { new CursorModelParameterValue { Value = "false", DisplayName = null }, new CursorModelParameterValue { Value = "true", DisplayName = ":icon-brain:" } } } },
                    Variants = new List<CursorModelVariant> { new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "false" } }, DisplayName = "Opus 4.5", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "true" } }, DisplayName = "Opus 4.5", IsDefault = true } },
                    SortOrder = 10,
                    CreatedAt = cursorSeedTimestamp,
                    UpdatedAt = cursorSeedTimestamp,
                    IsDeleted = false,
                },
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-00000000000c"),
                    Slug = "gpt-5.2",
                    DisplayName = "GPT-5.2",
                    Description = (string?)null,
                    IsActive = true,
                    Aliases = new List<string> { "gpt" },
                    Parameters = new List<CursorModelParameter> { new CursorModelParameter { Id = "reasoning", DisplayName = "Reasoning", Values = new List<CursorModelParameterValue> { new CursorModelParameterValue { Value = "low", DisplayName = "Low" }, new CursorModelParameterValue { Value = "medium", DisplayName = "Medium" }, new CursorModelParameterValue { Value = "high", DisplayName = "High" }, new CursorModelParameterValue { Value = "extra-high", DisplayName = "Extra High" } } }, new CursorModelParameter { Id = "fast", DisplayName = "Fast", Values = new List<CursorModelParameterValue> { new CursorModelParameterValue { Value = "false", DisplayName = null }, new CursorModelParameterValue { Value = "true", DisplayName = "Fast" } } } },
                    Variants = new List<CursorModelVariant> { new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "low" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "GPT-5.2", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "low" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "GPT-5.2", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "medium" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "GPT-5.2", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "medium" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "GPT-5.2", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "high" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "GPT-5.2", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "high" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "GPT-5.2", IsDefault = true }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "extra-high" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "GPT-5.2", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "extra-high" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "GPT-5.2", IsDefault = false } },
                    SortOrder = 11,
                    CreatedAt = cursorSeedTimestamp,
                    UpdatedAt = cursorSeedTimestamp,
                    IsDeleted = false,
                },
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-00000000000d"),
                    Slug = "gemini-3.1-pro",
                    DisplayName = "Gemini 3.1 Pro",
                    Description = (string?)null,
                    IsActive = true,
                    Aliases = new List<string> { "gemini-latest", "gemini-pro-latest", "gemini", "gemini-pro" },
                    Parameters = new List<CursorModelParameter>(),
                    Variants = new List<CursorModelVariant> { new CursorModelVariant { Params = new List<CursorModelVariantParam> {  }, DisplayName = "Gemini 3.1 Pro", IsDefault = true } },
                    SortOrder = 12,
                    CreatedAt = cursorSeedTimestamp,
                    UpdatedAt = cursorSeedTimestamp,
                    IsDeleted = false,
                },
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-00000000000e"),
                    Slug = "gpt-5.4-mini",
                    DisplayName = "GPT-5.4 Mini",
                    Description = (string?)null,
                    IsActive = true,
                    Aliases = new List<string> { "gpt-mini-latest", "gpt-mini" },
                    Parameters = new List<CursorModelParameter> { new CursorModelParameter { Id = "reasoning", DisplayName = "Reasoning", Values = new List<CursorModelParameterValue> { new CursorModelParameterValue { Value = "none", DisplayName = "None" }, new CursorModelParameterValue { Value = "low", DisplayName = "Low" }, new CursorModelParameterValue { Value = "medium", DisplayName = "Medium" }, new CursorModelParameterValue { Value = "high", DisplayName = "High" }, new CursorModelParameterValue { Value = "xhigh", DisplayName = "Extra High" } } } },
                    Variants = new List<CursorModelVariant> { new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "none" } }, DisplayName = "GPT-5.4 Mini", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "low" } }, DisplayName = "GPT-5.4 Mini", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "medium" } }, DisplayName = "GPT-5.4 Mini", IsDefault = true }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "high" } }, DisplayName = "GPT-5.4 Mini", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "xhigh" } }, DisplayName = "GPT-5.4 Mini", IsDefault = false } },
                    SortOrder = 13,
                    CreatedAt = cursorSeedTimestamp,
                    UpdatedAt = cursorSeedTimestamp,
                    IsDeleted = false,
                },
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-00000000000f"),
                    Slug = "gpt-5.4-nano",
                    DisplayName = "GPT-5.4 Nano",
                    Description = (string?)null,
                    IsActive = true,
                    Aliases = new List<string> { "gpt-nano-latest", "gpt-nano" },
                    Parameters = new List<CursorModelParameter> { new CursorModelParameter { Id = "reasoning", DisplayName = "Reasoning", Values = new List<CursorModelParameterValue> { new CursorModelParameterValue { Value = "none", DisplayName = "None" }, new CursorModelParameterValue { Value = "low", DisplayName = "Low" }, new CursorModelParameterValue { Value = "medium", DisplayName = "Medium" }, new CursorModelParameterValue { Value = "high", DisplayName = "High" }, new CursorModelParameterValue { Value = "xhigh", DisplayName = "Extra High" } } } },
                    Variants = new List<CursorModelVariant> { new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "none" } }, DisplayName = "GPT-5.4 Nano", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "low" } }, DisplayName = "GPT-5.4 Nano", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "medium" } }, DisplayName = "GPT-5.4 Nano", IsDefault = true }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "high" } }, DisplayName = "GPT-5.4 Nano", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "xhigh" } }, DisplayName = "GPT-5.4 Nano", IsDefault = false } },
                    SortOrder = 14,
                    CreatedAt = cursorSeedTimestamp,
                    UpdatedAt = cursorSeedTimestamp,
                    IsDeleted = false,
                },
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-000000000010"),
                    Slug = "claude-haiku-4-5",
                    DisplayName = "Haiku 4.5",
                    Description = (string?)null,
                    IsActive = true,
                    Aliases = new List<string> { "haiku-latest", "haiku", "haiku-4.5", "haiku-4-5" },
                    Parameters = new List<CursorModelParameter> { new CursorModelParameter { Id = "thinking", DisplayName = "Thinking", Values = new List<CursorModelParameterValue> { new CursorModelParameterValue { Value = "false", DisplayName = null }, new CursorModelParameterValue { Value = "true", DisplayName = ":icon-brain:" } } } },
                    Variants = new List<CursorModelVariant> { new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "false" } }, DisplayName = "Haiku 4.5", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "true" } }, DisplayName = "Haiku 4.5", IsDefault = true } },
                    SortOrder = 15,
                    CreatedAt = cursorSeedTimestamp,
                    UpdatedAt = cursorSeedTimestamp,
                    IsDeleted = false,
                },
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-000000000011"),
                    Slug = "grok-4.3",
                    DisplayName = "Grok 4.3",
                    Description = (string?)null,
                    IsActive = true,
                    Aliases = new List<string> { "grok-latest", "grok" },
                    Parameters = new List<CursorModelParameter> { new CursorModelParameter { Id = "context", DisplayName = "Context", Values = new List<CursorModelParameterValue> { new CursorModelParameterValue { Value = "200k", DisplayName = "200K" }, new CursorModelParameterValue { Value = "1m", DisplayName = "1M" } } } },
                    Variants = new List<CursorModelVariant> { new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "context", Value = "200k" } }, DisplayName = "Grok 4.3", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "context", Value = "1m" } }, DisplayName = "Grok 4.3", IsDefault = true } },
                    SortOrder = 16,
                    CreatedAt = cursorSeedTimestamp,
                    UpdatedAt = cursorSeedTimestamp,
                    IsDeleted = false,
                },
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-000000000012"),
                    Slug = "claude-sonnet-4-5",
                    DisplayName = "Sonnet 4.5",
                    Description = (string?)null,
                    IsActive = true,
                    Aliases = new List<string> { "sonnet", "sonnet-4.5", "sonnet-4-5" },
                    Parameters = new List<CursorModelParameter> { new CursorModelParameter { Id = "thinking", DisplayName = "Thinking", Values = new List<CursorModelParameterValue> { new CursorModelParameterValue { Value = "false", DisplayName = null }, new CursorModelParameterValue { Value = "true", DisplayName = ":icon-brain:" } } }, new CursorModelParameter { Id = "context", DisplayName = "Context", Values = new List<CursorModelParameterValue> { new CursorModelParameterValue { Value = "200k", DisplayName = "200K" } } } },
                    Variants = new List<CursorModelVariant> { new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "200k" } }, DisplayName = "Sonnet 4.5", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "200k" } }, DisplayName = "Sonnet 4.5", IsDefault = true } },
                    SortOrder = 17,
                    CreatedAt = cursorSeedTimestamp,
                    UpdatedAt = cursorSeedTimestamp,
                    IsDeleted = false,
                },
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-000000000013"),
                    Slug = "gpt-5.2-codex",
                    DisplayName = "Codex 5.2",
                    Description = (string?)null,
                    IsActive = true,
                    Aliases = new List<string> { "codex", "codex-5.2" },
                    Parameters = new List<CursorModelParameter> { new CursorModelParameter { Id = "reasoning", DisplayName = "Reasoning", Values = new List<CursorModelParameterValue> { new CursorModelParameterValue { Value = "low", DisplayName = "Low" }, new CursorModelParameterValue { Value = "medium", DisplayName = "Medium" }, new CursorModelParameterValue { Value = "high", DisplayName = "High" }, new CursorModelParameterValue { Value = "extra-high", DisplayName = "Extra High" } } }, new CursorModelParameter { Id = "fast", DisplayName = "Fast", Values = new List<CursorModelParameterValue> { new CursorModelParameterValue { Value = "false", DisplayName = null }, new CursorModelParameterValue { Value = "true", DisplayName = "Fast" } } } },
                    Variants = new List<CursorModelVariant> { new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "low" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Codex 5.2", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "low" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Codex 5.2", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "medium" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Codex 5.2", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "medium" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Codex 5.2", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "high" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Codex 5.2", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "high" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Codex 5.2", IsDefault = true }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "extra-high" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Codex 5.2", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "extra-high" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Codex 5.2", IsDefault = false } },
                    SortOrder = 18,
                    CreatedAt = cursorSeedTimestamp,
                    UpdatedAt = cursorSeedTimestamp,
                    IsDeleted = false,
                },
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-000000000014"),
                    Slug = "gpt-5.1-codex-max",
                    DisplayName = "Codex 5.1 Max",
                    Description = (string?)null,
                    IsActive = true,
                    Aliases = new List<string> { "codex", "codex-5.1-max" },
                    Parameters = new List<CursorModelParameter> { new CursorModelParameter { Id = "reasoning", DisplayName = "Reasoning", Values = new List<CursorModelParameterValue> { new CursorModelParameterValue { Value = "low", DisplayName = "Low" }, new CursorModelParameterValue { Value = "medium", DisplayName = "Medium" }, new CursorModelParameterValue { Value = "high", DisplayName = "High" }, new CursorModelParameterValue { Value = "extra-high", DisplayName = "Extra High" } } }, new CursorModelParameter { Id = "fast", DisplayName = "Fast", Values = new List<CursorModelParameterValue> { new CursorModelParameterValue { Value = "false", DisplayName = null }, new CursorModelParameterValue { Value = "true", DisplayName = "Fast" } } } },
                    Variants = new List<CursorModelVariant> { new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "low" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Codex 5.1 Max", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "low" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Codex 5.1 Max", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "medium" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Codex 5.1 Max", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "medium" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Codex 5.1 Max", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "high" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Codex 5.1 Max", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "high" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Codex 5.1 Max", IsDefault = true }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "extra-high" }, new CursorModelVariantParam { Id = "fast", Value = "false" } }, DisplayName = "Codex 5.1 Max", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "extra-high" }, new CursorModelVariantParam { Id = "fast", Value = "true" } }, DisplayName = "Codex 5.1 Max", IsDefault = false } },
                    SortOrder = 19,
                    CreatedAt = cursorSeedTimestamp,
                    UpdatedAt = cursorSeedTimestamp,
                    IsDeleted = false,
                },
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-000000000015"),
                    Slug = "gpt-5.1",
                    DisplayName = "GPT-5.1",
                    Description = (string?)null,
                    IsActive = true,
                    Aliases = new List<string> { "gpt" },
                    Parameters = new List<CursorModelParameter> { new CursorModelParameter { Id = "reasoning", DisplayName = "Reasoning", Values = new List<CursorModelParameterValue> { new CursorModelParameterValue { Value = "low", DisplayName = "Low" }, new CursorModelParameterValue { Value = "medium", DisplayName = "Medium" }, new CursorModelParameterValue { Value = "high", DisplayName = "High" } } } },
                    Variants = new List<CursorModelVariant> { new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "low" } }, DisplayName = "GPT-5.1", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "medium" } }, DisplayName = "GPT-5.1", IsDefault = true }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "high" } }, DisplayName = "GPT-5.1", IsDefault = false } },
                    SortOrder = 20,
                    CreatedAt = cursorSeedTimestamp,
                    UpdatedAt = cursorSeedTimestamp,
                    IsDeleted = false,
                },
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-000000000016"),
                    Slug = "gemini-3-flash",
                    DisplayName = "Gemini 3 Flash",
                    Description = (string?)null,
                    IsActive = true,
                    Aliases = new List<string>(),
                    Parameters = new List<CursorModelParameter>(),
                    Variants = new List<CursorModelVariant> { new CursorModelVariant { Params = new List<CursorModelVariantParam> {  }, DisplayName = "Gemini 3 Flash", IsDefault = true } },
                    SortOrder = 21,
                    CreatedAt = cursorSeedTimestamp,
                    UpdatedAt = cursorSeedTimestamp,
                    IsDeleted = false,
                },
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-000000000017"),
                    Slug = "gemini-3.5-flash",
                    DisplayName = "Gemini 3.5 Flash",
                    Description = (string?)null,
                    IsActive = true,
                    Aliases = new List<string> { "gemini-flash-latest", "gemini-flash" },
                    Parameters = new List<CursorModelParameter>(),
                    Variants = new List<CursorModelVariant> { new CursorModelVariant { Params = new List<CursorModelVariantParam> {  }, DisplayName = "Gemini 3.5 Flash", IsDefault = true } },
                    SortOrder = 22,
                    CreatedAt = cursorSeedTimestamp,
                    UpdatedAt = cursorSeedTimestamp,
                    IsDeleted = false,
                },
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-000000000018"),
                    Slug = "gpt-5.1-codex-mini",
                    DisplayName = "Codex 5.1 Mini",
                    Description = (string?)null,
                    IsActive = true,
                    Aliases = new List<string> { "codex-mini-latest", "codex-mini" },
                    Parameters = new List<CursorModelParameter> { new CursorModelParameter { Id = "reasoning", DisplayName = "Reasoning", Values = new List<CursorModelParameterValue> { new CursorModelParameterValue { Value = "low", DisplayName = "Low" }, new CursorModelParameterValue { Value = "medium", DisplayName = "Medium" }, new CursorModelParameterValue { Value = "high", DisplayName = "High" } } } },
                    Variants = new List<CursorModelVariant> { new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "low" } }, DisplayName = "Codex 5.1 Mini", IsDefault = false }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "medium" } }, DisplayName = "Codex 5.1 Mini", IsDefault = true }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "reasoning", Value = "high" } }, DisplayName = "Codex 5.1 Mini", IsDefault = false } },
                    SortOrder = 23,
                    CreatedAt = cursorSeedTimestamp,
                    UpdatedAt = cursorSeedTimestamp,
                    IsDeleted = false,
                },
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-000000000019"),
                    Slug = "claude-sonnet-4",
                    DisplayName = "Sonnet 4",
                    Description = (string?)null,
                    IsActive = true,
                    Aliases = new List<string> { "sonnet", "sonnet-4" },
                    Parameters = new List<CursorModelParameter> { new CursorModelParameter { Id = "thinking", DisplayName = "Thinking", Values = new List<CursorModelParameterValue> { new CursorModelParameterValue { Value = "false", DisplayName = null }, new CursorModelParameterValue { Value = "true", DisplayName = ":icon-brain:" } } }, new CursorModelParameter { Id = "context", DisplayName = "Context", Values = new List<CursorModelParameterValue> { new CursorModelParameterValue { Value = "200k", DisplayName = "200K" } } } },
                    Variants = new List<CursorModelVariant> { new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "false" }, new CursorModelVariantParam { Id = "context", Value = "200k" } }, DisplayName = "Sonnet 4", IsDefault = true }, new CursorModelVariant { Params = new List<CursorModelVariantParam> { new CursorModelVariantParam { Id = "thinking", Value = "true" }, new CursorModelVariantParam { Id = "context", Value = "200k" } }, DisplayName = "Sonnet 4", IsDefault = false } },
                    SortOrder = 24,
                    CreatedAt = cursorSeedTimestamp,
                    UpdatedAt = cursorSeedTimestamp,
                    IsDeleted = false,
                },
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-00000000001a"),
                    Slug = "gpt-5-mini",
                    DisplayName = "GPT-5 Mini",
                    Description = (string?)null,
                    IsActive = true,
                    Aliases = new List<string> { "gpt-mini" },
                    Parameters = new List<CursorModelParameter>(),
                    Variants = new List<CursorModelVariant> { new CursorModelVariant { Params = new List<CursorModelVariantParam> {  }, DisplayName = "GPT-5 Mini", IsDefault = true } },
                    SortOrder = 25,
                    CreatedAt = cursorSeedTimestamp,
                    UpdatedAt = cursorSeedTimestamp,
                    IsDeleted = false,
                },
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-00000000001b"),
                    Slug = "gemini-2.5-flash",
                    DisplayName = "Gemini 2.5 Flash",
                    Description = (string?)null,
                    IsActive = true,
                    Aliases = new List<string> { "gemini-flash" },
                    Parameters = new List<CursorModelParameter>(),
                    Variants = new List<CursorModelVariant> { new CursorModelVariant { Params = new List<CursorModelVariantParam> {  }, DisplayName = "Gemini 2.5 Flash", IsDefault = true } },
                    SortOrder = 26,
                    CreatedAt = cursorSeedTimestamp,
                    UpdatedAt = cursorSeedTimestamp,
                    IsDeleted = false,
                },
                new
                {
                    Id = new Guid("b0000000-0000-0000-0000-00000000001c"),
                    Slug = "kimi-k2.5",
                    DisplayName = "Kimi K2.5",
                    Description = (string?)null,
                    IsActive = true,
                    Aliases = new List<string> { "kimi-latest", "kimi" },
                    Parameters = new List<CursorModelParameter>(),
                    Variants = new List<CursorModelVariant> { new CursorModelVariant { Params = new List<CursorModelVariantParam> {  }, DisplayName = "Kimi K2.5", IsDefault = true } },
                    SortOrder = 27,
                    CreatedAt = cursorSeedTimestamp,
                    UpdatedAt = cursorSeedTimestamp,
                    IsDeleted = false,
                }
            );
        });
    }
}

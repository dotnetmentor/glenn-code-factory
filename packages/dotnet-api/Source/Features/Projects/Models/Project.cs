using Source.Features.CursorModels.Models;
using Source.Features.GitHub.Models;
using Source.Features.ProjectTemplates.Models;
using Source.Features.Projects.Events;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.Users.Models;
using Source.Features.Workspaces.Models;
using Source.Shared;
using Source.Shared.Events;
using Source.Shared.Results;

namespace Source.Features.Projects.Models;

/// <summary>
/// A connected GitHub repo inside a <see cref="Workspace"/>.
/// </summary>
public class Project : Entity, IAuditable, ISoftDelete
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Workspace Workspace { get; set; } = null!;
    public string OwnerUserId { get; set; } = string.Empty;
    public User Owner { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public string GithubRepoOwner { get; set; } = string.Empty;
    public string GithubRepoName { get; set; } = string.Empty;
    public Guid? GithubInstallationId { get; set; }
    public GithubInstallation? GithubInstallation { get; set; }

    public int PreviewPort { get; set; } = DefaultPreviewPort;
    public const int DefaultPreviewPort = 5173;
    public const int MinPreviewPort = 1;
    public const int MaxPreviewPort = 65535;

    public string RuntimeCpuKind { get; set; } = DefaultRuntimeCpuKind;
    public int RuntimeCpus { get; set; } = DefaultRuntimeCpus;
    public int RuntimeMemoryMb { get; set; } = DefaultRuntimeMemoryMb;
    public int RuntimeVolumeSizeGb { get; set; } = DefaultRuntimeVolumeSizeGb;

    public const string DefaultRuntimeCpuKind = "performance";
    public const int DefaultRuntimeCpus = 2;
    public const int DefaultRuntimeMemoryMb = 4096;
    public const int DefaultRuntimeVolumeSizeGb = 10;
    public const int MinRuntimeCpus = 1;
    public const int MaxRuntimeCpus = 16;
    public const int MinRuntimeMemoryMb = 256;
    public const int MaxRuntimeMemoryMb = 262144;
    public const int MinRuntimeVolumeSizeGb = 1;
    public const int MaxRuntimeVolumeSizeGb = 500;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }

    public ICollection<ProjectRuntime> Runtimes { get; set; } = new List<ProjectRuntime>();
    public ICollection<ProjectBranch> Branches { get; set; } = new List<ProjectBranch>();
    public ProjectAgentPermissions? AgentPermissions { get; set; }

    /// <summary>Default Cursor model for new sessions on this project.</summary>
    public Guid? ModelId { get; private set; }
    public virtual CursorModel? Model { get; private set; }

    /// <summary>BYOK Cursor API key, encrypted under the project's DEK.</summary>
    public string? EncryptedCursorApiKey { get; set; }

    public Guid? TemplateId { get; set; }
    public ProjectTemplate? Template { get; set; }

    /// <summary>
    /// Project-level runtime spec — source of truth for what's installed
    /// across every runtime under this project. JSON of <c>RuntimeSpecV3</c>
    /// (preset-based: <c>{ kind, name, values }</c> services that the server-
    /// side <c>IPresetExpander</c> renders to the V2 shape the daemon
    /// consumes).
    ///
    /// <para>Moved from <c>ProjectRuntime.Spec</c> per the
    /// <c>project-level-runtime-spec</c> spec: one project, one spec, all
    /// branches inherit. Approving a proposal writes here; bootstrap reads
    /// from here on every cold boot / wake / respawn (expanding V3→V2 on the
    /// way out), so new branches converge to the project's current state
    /// without re-approval.</para>
    ///
    /// <para>Null on Phase 1 / pre-curation projects and on rows wiped by the
    /// V2→V3 cutover migration; treat as empty when reading (the V3 read
    /// handler hands back an empty <c>RuntimeSpecV3</c> in that case).</para>
    /// </summary>
    public string? Spec { get; set; }

    /// <summary>
    /// Bump-on-write counter for <see cref="Spec"/>. Starts at 1 for new
    /// projects; every approval / edit increments by one so reactors can
    /// detect "is this newer than what I last applied?" cheaply without a
    /// full body diff.
    /// </summary>
    public int SpecVersion { get; set; } = 1;

    public Result SetRuntimeSpec(string cpuKind, int cpus, int memoryMb, int volumeSizeGb)
    {
        var normalisedCpuKind = (cpuKind ?? string.Empty).Trim().ToLowerInvariant();
        if (normalisedCpuKind != "shared" && normalisedCpuKind != "performance")
        {
            return Result.Failure("invalid_cpu_kind");
        }

        var allowedCpus = new[] { 1, 2, 4, 8, 16 };
        if (Array.IndexOf(allowedCpus, cpus) < 0)
        {
            return Result.Failure("invalid_cpu_count");
        }

        if (memoryMb < MinRuntimeMemoryMb || memoryMb > MaxRuntimeMemoryMb)
        {
            return Result.Failure("invalid_memory_mb");
        }

        if (volumeSizeGb < MinRuntimeVolumeSizeGb || volumeSizeGb > MaxRuntimeVolumeSizeGb)
        {
            return Result.Failure("invalid_volume_size_gb");
        }

        if (normalisedCpuKind == "performance" && memoryMb < 2048 * cpus)
        {
            return Result.Failure("performance_memory_too_low");
        }

        if (string.Equals(RuntimeCpuKind, normalisedCpuKind, StringComparison.Ordinal)
            && RuntimeCpus == cpus
            && RuntimeMemoryMb == memoryMb
            && RuntimeVolumeSizeGb == volumeSizeGb)
        {
            return Result.Success();
        }

        RuntimeCpuKind = normalisedCpuKind;
        RuntimeCpus = cpus;
        RuntimeMemoryMb = memoryMb;
        RuntimeVolumeSizeGb = volumeSizeGb;
        return Result.Success();
    }

    public void MarkCreated()
    {
        if (GithubInstallationId is not { } installationId)
        {
            throw new InvalidOperationException(
                "Project.MarkCreated requires a non-null GithubInstallationId.");
        }

        RaiseDomainEvent(new ProjectCreated(
            Id,
            WorkspaceId,
            OwnerUserId,
            Name,
            GithubRepoOwner,
            GithubRepoName,
            installationId));
    }

    public const int MaxNameLength = 100;

    public Result Rename(string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            return Result.Failure("name_required");
        }

        var trimmed = newName.Trim();
        if (trimmed.Length > MaxNameLength)
        {
            return Result.Failure("name_too_long");
        }

        if (string.Equals(Name, trimmed, StringComparison.Ordinal))
        {
            return Result.Success();
        }

        var oldName = Name;
        Name = trimmed;
        RaiseDomainEvent(new ProjectRenamed(Id, oldName, trimmed));
        return Result.Success();
    }

    public Result SetPreviewPort(int port)
    {
        if (port < MinPreviewPort || port > MaxPreviewPort)
        {
            return Result.Failure("invalid_preview_port");
        }

        if (PreviewPort == port)
        {
            return Result.Success();
        }

        PreviewPort = port;
        return Result.Success();
    }

    public Result SetModel(Guid? modelId)
    {
        if (ModelId == modelId)
        {
            return Result.Success();
        }

        var oldId = ModelId;
        ModelId = modelId;
        RaiseDomainEvent(new ProjectDefaultModelChanged(Id, oldId, modelId));
        return Result.Success();
    }

    public Result SetEncryptedCursorApiKey(string? envelope)
    {
        if (string.Equals(EncryptedCursorApiKey, envelope, StringComparison.Ordinal))
        {
            return Result.Success();
        }

        EncryptedCursorApiKey = envelope;
        return Result.Success();
    }

    public void MarkDeleted()
    {
        if (IsDeleted)
        {
            return;
        }

        IsDeleted = true;
        RaiseDomainEvent(new ProjectDeleted(Id));
    }
}

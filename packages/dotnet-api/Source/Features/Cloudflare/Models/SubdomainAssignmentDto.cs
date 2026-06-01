namespace Source.Features.Cloudflare.Models;

/// <summary>
/// Read-side projection of a <see cref="SubdomainAssignment"/>. Never carries
/// the tunnel token — that secret stays on the server.
///
/// <para>The <c>AssignedTo*</c> fields are populated when
/// <see cref="AssignedBranchId"/> is non-null AND the join to
/// <c>ProjectBranches</c> + <c>Projects</c> succeeds. If the branch row has
/// been hard-deleted (rare; branches are soft-delete-only in v1) the
/// AssignedBranchId stays set but the display fields fall back to null.</para>
/// </summary>
public record SubdomainAssignmentDto
{
    public required Guid Id { get; init; }
    public required string Hostname { get; init; }
    public required string Subdomain { get; init; }
    public required SubdomainStatus Status { get; init; }
    public required DateTime CreatedAt { get; init; }

    /// <summary>Branch id if assigned; null while the row is in the pool.</summary>
    public Guid? AssignedBranchId { get; init; }
    public DateTime? AssignedAt { get; init; }

    /// <summary>Branch name (e.g. <c>"feature/checkout"</c>) when assigned. Null otherwise.</summary>
    public string? AssignedToBranchName { get; init; }

    /// <summary>Project id (when assigned).</summary>
    public Guid? AssignedToProjectId { get; init; }

    /// <summary>Project name (when assigned).</summary>
    public string? AssignedToProjectName { get; init; }
}

/// <summary>
/// Response shape for <c>POST /api/cloudflare/subdomains/batch</c>. Lists the
/// rows that were successfully persisted (note: even partial success is still
/// useful to the operator) along with summary counts.
/// </summary>
public record BatchCreateSubdomainsResponse
{
    public required int SuccessCount { get; init; }
    public required int FailedCount { get; init; }
    public required IReadOnlyList<SubdomainAssignmentDto> Items { get; init; }
}

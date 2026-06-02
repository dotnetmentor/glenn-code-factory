using Source.Shared;
using Source.Shared.Events;

namespace Source.Features.Cloudflare.Models;

/// <summary>
/// A single row in the preview-subdomain pool. Each row represents one
/// Cloudflare Tunnel + DNS CNAME pre-provisioned against the configured base
/// domain — created in bulk by the admin via the "Batch create" action and
/// atomically claimed one-at-a-time by branch creation.
///
/// <para><b>Lifecycle.</b> Rows progress strictly forwards through
/// <see cref="SubdomainStatus"/>: <c>Available → Assigned → Releasing</c> and
/// then are destroyed. Released subdomains are never returned to the pool —
/// the destroy-and-never-reuse rule is a security invariant (a leaked URL must
/// not silently start pointing at a new tenant's environment).</para>
///
/// <para><b>Branch FK.</b> <see cref="AssignedBranchId"/> is intentionally a
/// plain <c>Guid?</c> with no foreign-key constraint at this phase. Phase 1
/// builds the pool in isolation; Phase 3 is where branch creation invokes
/// <c>AssignSubdomainToBranchCommand</c> and we wire the relationship to
/// <see cref="Source.Features.Projects.Models.ProjectBranch"/>. Keeping the FK
/// off for now lets Phase 1 ship without the cross-feature coupling.</para>
///
/// <para><b>Tunnel token.</b> <see cref="TunnelToken"/> is the secret the
/// runtime's <c>cloudflared</c> sidecar uses to dial back into Cloudflare. It
/// is encrypted at rest (same AES-256-GCM envelope every other secret in this
/// codebase uses) — never logged, never returned by queries.</para>
///
/// <para>Auditable + soft-deletable so the admin trail survives a "delete and
/// reprovision" pass.</para>
/// </summary>
public class SubdomainAssignment : Entity, IAuditable, ISoftDelete
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Fully-qualified hostname under the configured base domain, e.g.
    /// <c>kj4m9x2p.example.com</c>. Globally unique — DB enforces.
    /// </summary>
    public string Hostname { get; set; } = string.Empty;

    /// <summary>
    /// The 8-char random prefix only (no dot, no base domain). Stored alongside
    /// <see cref="Hostname"/> for convenience — useful when rendering and when
    /// regenerating the hostname after a base-domain change (future work).
    /// </summary>
    public string Subdomain { get; set; } = string.Empty;

    /// <summary>
    /// Cloudflare-side tunnel id (uuid string). Returned by
    /// <c>POST /accounts/{account_id}/cfd_tunnel</c>. Stored cleartext —
    /// it's an opaque identifier, not a credential.
    /// </summary>
    public string TunnelId { get; set; } = string.Empty;

    /// <summary>
    /// Encrypted tunnel token — the base64-AES-256-GCM envelope returned by
    /// <see cref="Source.Features.SystemSettings.Services.ISystemSettingsCipher.Encrypt"/>.
    /// The cleartext token is what <c>cloudflared</c> uses to authenticate
    /// against Cloudflare in Phase 4. Never returned by queries or logged.
    /// </summary>
    public string TunnelToken { get; set; } = string.Empty;

    /// <summary>Lifecycle state. Indexed for the "next available" pool query.</summary>
    public SubdomainStatus Status { get; set; } = SubdomainStatus.Available;

    /// <summary>
    /// Branch id (when <see cref="Status"/> = <see cref="SubdomainStatus.Assigned"/>).
    /// No FK constraint at this phase — see class-level XML doc.
    /// </summary>
    public Guid? AssignedBranchId { get; set; }

    /// <summary>UTC timestamp the row transitioned to <see cref="SubdomainStatus.Assigned"/>.</summary>
    public DateTime? AssignedAt { get; set; }

    // -------- IAuditable --------
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // -------- ISoftDelete --------
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
}

using Source.Features.Cloudflare.Models;
using Source.Features.Conversations.Models;
using Source.Features.Projects.Events;
using Source.Features.RuntimeLifecycle.Models;
using Source.Shared;
using Source.Shared.Events;

namespace Source.Features.Projects.Models;

/// <summary>
/// A first-class branch row inside a <see cref="Project"/>. Runtime granularity
/// in the e2e-smoketest spec is <c>(Project, ProjectBranch)</c> — branches are
/// no longer modelled as free-form strings on <see cref="ProjectRuntime"/> /
/// <see cref="Conversation"/>.
///
/// <list type="bullet">
///   <item><see cref="ProjectId"/> is a real FK to <see cref="Project"/> —
///         deleting a project cascades to its branch rows.</item>
///   <item><see cref="Name"/> is the git branch name (e.g. <c>"main"</c>,
///         <c>"feature/redesign"</c>). Unique per project, enforced by a
///         composite index on <c>(ProjectId, Name)</c>.</item>
///   <item><see cref="IsDefault"/> marks the project's default branch — the
///         one created automatically with the project. Exactly one row per
///         project should carry this flag; enforcement lives in the command
///         handlers (a partial unique index can be added later if drift becomes
///         a problem).</item>
///   <item>Auditable but NOT soft-deletable: a runtime / conversation pinned
///         to a branch keeps that pairing for life, so deleting the branch row
///         out from under them would orphan FKs. Lifecycle is "create on
///         demand, never delete in v1".</item>
/// </list>
///
/// <para>No domain events on this card — branch creation is a straightforward
/// child of the existing <c>ProjectCreated</c> flow. Behaviour (rename, mark
/// default, etc.) arrives in a follow-up card if needed.</para>
/// </summary>
public class ProjectBranch : Entity, IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>FK to the owning <see cref="Project"/>. Required.</summary>
    public Guid ProjectId { get; set; }

    /// <summary>Navigation to the owning project.</summary>
    public Project Project { get; set; } = null!;

    /// <summary>
    /// Git branch name — e.g. <c>"main"</c>, <c>"feature/redesign"</c>. Required.
    /// Max 250 chars (git's practical refname limit). Unique per project.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// True for the project's default branch (the one created with the project).
    /// Exactly one row per project should carry this flag — enforced in the
    /// command handlers for now.
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// Soft-archive flag. Archived branches are hidden from sidebars / branch
    /// pickers and their runtimes are suspended on archive; history /
    /// conversation queries still see them so users can read past work.
    /// Reversible via <see cref="Unarchive"/>.
    ///
    /// <para>Setter is <c>private</c> — the only legal mutation paths are the
    /// rich entity methods <see cref="Archive"/> / <see cref="Unarchive"/> so
    /// the invariant pairing (IsArchived ↔ ArchivedAt) and the
    /// <c>ProjectBranchArchived</c> / <c>ProjectBranchUnarchived</c> domain
    /// events stay in lock-step.</para>
    /// </summary>
    public bool IsArchived { get; private set; }

    /// <summary>
    /// UTC timestamp the branch was archived. Paired with
    /// <see cref="IsArchived"/>: <c>null</c> when active, populated on
    /// <see cref="Archive"/>, re-cleared on <see cref="Unarchive"/>. Useful for
    /// the settings UI to render "Archived 3 days ago" without an extra audit
    /// query.
    /// </summary>
    public DateTime? ArchivedAt { get; private set; }

    // -------- IAuditable --------
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Runtimes pinned to this <c>(Project, Branch)</c> pair. A runtime is
    /// pinned for life — there is no in-place branch switching. Populated via
    /// <see cref="ProjectRuntime.BranchId"/>.
    /// </summary>
    public ICollection<ProjectRuntime> Runtimes { get; set; } = new List<ProjectRuntime>();

    /// <summary>
    /// Conversations scoped to this branch. Populated via
    /// <see cref="Conversation.BranchId"/>.
    /// </summary>
    public ICollection<Conversation> Conversations { get; set; } = new List<Conversation>();

    /// <summary>
    /// The preview-subdomain row this branch claimed at creation time
    /// (cloudflare-tunnel-preview Phase 3). One row per branch — branch
    /// creation atomically claims an Available pool row and binds it here;
    /// branch deletion flips the row's <see cref="SubdomainStatus"/> to
    /// <see cref="SubdomainStatus.Releasing"/> so Phase 4 can tear down the
    /// Cloudflare side. <c>null</c> means "this branch pre-dates the pool" or
    /// (transiently) "claim has not been flushed yet".
    ///
    /// <para>FK lives on <see cref="SubdomainAssignment.AssignedBranchId"/>
    /// with <see cref="Microsoft.EntityFrameworkCore.DeleteBehavior.SetNull"/>:
    /// hard-deleting a branch nulls the FK on the (rare) surviving subdomain
    /// row rather than cascading the pool row away — released rows are
    /// preserved for the destroy-and-never-reuse audit trail.</para>
    /// </summary>
    public SubdomainAssignment? AssignedSubdomain { get; set; }

    /// <summary>
    /// Raise <see cref="BranchCopied"/> on the new branch row just before the
    /// orchestrator's single <c>SaveChangesAsync</c> commits. Kept on the
    /// entity (not the handler) so audit traceability and the event live next
    /// to the rest of the branch's behaviour — same pattern as
    /// <c>Project.MarkCreated()</c>. Idempotency is not enforced here; the
    /// caller (<c>CopyBranchHandler</c>) only invokes once per orchestration.
    /// </summary>
    public void RaiseBranchCopied(Guid sourceBranchId, Guid newRuntimeId, string forkedVolumeId)
    {
        RaiseDomainEvent(new BranchCopied(Id, sourceBranchId, newRuntimeId, forkedVolumeId));
    }

    /// <summary>
    /// Flip this branch to the archived state. Idempotent: a no-op if already
    /// archived. Refuses to archive the project's default branch
    /// (<see cref="IsDefault"/>) — the handler already gates on this with a
    /// nicer error message; throwing here is belt-and-suspenders so callers
    /// can't bypass the rule by going around the handler.
    ///
    /// <para>Raises <c>ProjectBranchArchived</c> so downstream handlers (audit,
    /// telemetry, future runtime-suspend reactions) observe the transition
    /// after the orchestrator's <c>SaveChangesAsync</c> commits.</para>
    /// </summary>
    public void Archive()
    {
        if (IsArchived) return;
        if (IsDefault)
        {
            throw new InvalidOperationException(
                "The default branch cannot be archived.");
        }

        IsArchived = true;
        ArchivedAt = DateTime.UtcNow;
        RaiseDomainEvent(new ProjectBranchArchived(Id, ProjectId, Name));
    }

    /// <summary>
    /// Reverse <see cref="Archive"/>. Idempotent: a no-op if the branch is
    /// already active. Does NOT touch the runtime — a suspended runtime wakes
    /// naturally on the next user activity (the existing wake-on-connect
    /// path).
    ///
    /// <para>Raises <c>ProjectBranchUnarchived</c> for audit / telemetry.</para>
    /// </summary>
    public void Unarchive()
    {
        if (!IsArchived) return;

        IsArchived = false;
        ArchivedAt = null;
        RaiseDomainEvent(new ProjectBranchUnarchived(Id, ProjectId, Name));
    }
}

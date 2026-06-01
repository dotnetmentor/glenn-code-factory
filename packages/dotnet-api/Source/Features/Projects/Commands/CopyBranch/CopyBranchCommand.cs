using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Commands.CopyBranch;

/// <summary>
/// Fork an existing <see cref="Models.ProjectBranch"/> into a brand-new branch
/// that is bit-identical to the source at the moment of copy. The orchestrator
/// chains a GitHub ref creation, a Fly volume fork and the DB row inserts; if
/// any step fails the previously-completed steps are torn down in reverse so
/// the user never sees a half-cloned ghost branch.
///
/// <para>Powers <c>POST /api/projects/{projectId}/branches/{branchId}/copy</c>.
/// The new <see cref="RuntimeLifecycle.Models.ProjectRuntime"/> is left in
/// <c>Pending</c> state on success — the recurring <c>RuntimeProvisionerJob</c>
/// then takes it the rest of the way to <c>Online</c>, mirroring how
/// <c>CreateProjectHandler</c> hands off.</para>
///
/// <para><b>Authorisation gate.</b> Caller must be a member of the project's
/// workspace; non-members collapse to the <see cref="CopyBranchHandler.ForbiddenPrefix"/>
/// sentinel so the controller can return 403 without leaking project
/// existence. A missing source branch / runtime returns
/// <see cref="CopyBranchHandler.NotFoundPrefix"/> → 404 the same way.</para>
///
/// <para><b>Naming.</b> When <see cref="NewBranchName"/> is <c>null</c> the
/// handler auto-suffixes (<c>{source}-copy</c>, <c>-copy-2</c>, …) so a user
/// who hammers the button never has to deduplicate by hand. An explicit name
/// that collides with an existing branch is rejected with a validation error
/// before any Fly or GitHub resources are touched.</para>
///
/// <para><b>Services / spec picker.</b> Three mutually-exclusive paths (Scene 2
/// of <c>workspace-spec-catalog</c>):
/// <list type="bullet">
///   <item>Default (both flags null/false) — the new runtime carries the
///         source runtime's <c>Spec</c> verbatim. This is the bugfix from
///         Scene 9: before this card, the default forked a branch whose
///         <c>Spec = NULL</c> and the agent had to re-propose every service
///         from scratch.</item>
///   <item><see cref="CatalogSpecId"/> non-null — deep-copy the named
///         <c>WorkspaceSpec.Content</c> into the new runtime's <c>Spec</c>
///         (V2 schema). The catalog spec must belong to the same workspace
///         as the source runtime; cross-workspace ids return a 404 sentinel.</item>
///   <item><see cref="ForceBlankSpec"/> = <c>true</c> — the new runtime
///         starts with <c>Spec = NULL</c>. Explicit override for "I don't
///         want any services yet."</item>
/// </list>
/// If both flags are supplied the explicit-blank flag wins (defensive — the
/// frontend should never send both, but if it does, "blank" is the less
/// surprising outcome).</para>
/// </summary>
public sealed record CopyBranchCommand(
    Guid SourceBranchId,
    string? NewBranchName,
    string CallerUserId,
    Guid? CatalogSpecId = null,
    bool ForceBlankSpec = false
) : ICommand<Result<CopyBranchResult>>;

/// <summary>
/// Wire shape returned to the controller. The new runtime is <c>Pending</c> at
/// this point; the frontend subscribes to the existing SignalR runtime-state
/// channel to observe the Booting → Online transitions.
/// </summary>
public sealed record CopyBranchResult(
    Guid NewBranchId,
    Guid NewRuntimeId,
    string NewBranchName);

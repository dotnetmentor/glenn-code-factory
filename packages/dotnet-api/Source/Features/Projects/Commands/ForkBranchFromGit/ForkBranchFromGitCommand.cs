using Source.Features.RuntimeLifecycle.Models;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Commands.ForkBranchFromGit;

/// <summary>
/// "Create a new branch based on this git branch" — pushes a new git ref forked from
/// <paramref name="SourceGitBranchName"/>'s HEAD SHA, then provisions a fresh system
/// <see cref="Models.ProjectBranch"/> + <see cref="ProjectRuntime"/> for it.
///
/// <para>Powers <c>POST /api/projects/{projectId}/branches/fork-from-git</c> — the slow
/// path for forking when only a git branch exists. The fast path (fork a source system
/// branch's volume) lives in <c>CopyBranchCommand</c> and is untouched by this card.</para>
///
/// <para><b>Why a fresh clone, not a volume fork.</b> The source side here is "just a
/// git branch", there's no system branch and therefore no Fly volume to fork. The new
/// runtime boots from a fresh Fly volume that the daemon's bootstrap clones the new git
/// ref into on first start — same shape as the CreateProject onboarding path.</para>
///
/// <para><b>Compensation.</b> If the pushed GitHub ref succeeds but a later step fails
/// (volume/clock/DB write), the handler deletes the orphan ref so the user sees "nothing
/// was changed". Same compensation discipline as <c>CopyBranchHandler</c>.</para>
///
/// <para><b>Services / spec picker.</b> Mirrors the <c>CopyBranchCommand</c> picker, but
/// with a different default: there is no source <c>ProjectRuntime</c> to carry from on
/// this entry point (the source is "just a git branch"), so the default with both flags
/// null/false is a BLANK spec. Two-way choice:
/// <list type="bullet">
///   <item>Both flags null/false — new runtime starts with <c>Spec = NULL</c>.</item>
///   <item><see cref="CatalogSpecId"/> set — deep-copy the named workspace catalog
///         entry's content into the new runtime's <c>Spec</c>. Cross-workspace ids
///         collapse to a 404 sentinel.</item>
/// </list>
/// <see cref="ForceBlankSpec"/> is accepted for symmetry with <c>CopyBranchCommand</c>
/// even though it equals the default — keeping the wire shapes parallel makes the
/// frontend's "spec picker" component reusable across both fork dialogs.</para>
/// </summary>
public sealed record ForkBranchFromGitCommand(
    Guid ProjectId,
    string SourceGitBranchName,
    string NewBranchName,
    string CallerUserId,
    Guid? CatalogSpecId = null,
    bool ForceBlankSpec = false
) : ICommand<Result<ForkBranchFromGitResult>>;

/// <summary>
/// Wire-internal shape returned by the handler — controller maps to
/// <see cref="Controllers.ForkBranchFromGitResponse"/>.
/// </summary>
public sealed record ForkBranchFromGitResult(
    Guid BranchId,
    Guid RuntimeId,
    string NewBranchName,
    RuntimeState State);

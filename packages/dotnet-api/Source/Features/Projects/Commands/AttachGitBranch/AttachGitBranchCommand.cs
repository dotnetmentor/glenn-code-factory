using Source.Features.RuntimeLifecycle.Models;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Commands.AttachGitBranch;

/// <summary>
/// "Continue working on this git branch" — links an existing git branch on the project's
/// GitHub repo as a brand-new <see cref="Models.ProjectBranch"/> + <see cref="ProjectRuntime"/>.
/// No new git ref is pushed; the runtime boots from a fresh Fly volume which the daemon
/// clones from the existing git branch on first start.
///
/// <para>Powers <c>POST /api/projects/{projectId}/branches/attach</c> — the slow path
/// that runs when only a git branch exists. Compare with
/// <c>POST /api/projects/{projectId}/branches/{branchId}/copy</c> (the fast path: fork
/// a source system branch's volume) and the project-create onboarding atom.</para>
///
/// <para><b>Authorisation gate.</b> Caller must be a member of the project's workspace;
/// non-members and missing/soft-deleted projects collapse to a single 404 via the
/// <see cref="AttachGitBranchHandler.NotFoundPrefix"/> sentinel so existence cannot
/// be probed.</para>
///
/// <para><b>Idempotency.</b> If a non-deleted <see cref="Models.ProjectBranch"/> already
/// exists for this project with <c>Name == GitBranchName</c>, the handler returns
/// <see cref="AttachGitBranchHandler.AlreadyLinkedError"/> (controller → 409). The
/// frontend can then route the user to the existing system branch's workspace
/// directly instead of attempting to attach a second time.</para>
/// </summary>
public sealed record AttachGitBranchCommand(
    Guid ProjectId,
    string GitBranchName,
    string CallerUserId
) : ICommand<Result<AttachGitBranchResult>>;

/// <summary>
/// Wire-internal shape returned by the handler — the controller maps it to
/// <see cref="Controllers.AttachGitBranchResponse"/> with the runtime state stamped in.
/// </summary>
public sealed record AttachGitBranchResult(
    Guid BranchId,
    Guid RuntimeId,
    RuntimeState State);

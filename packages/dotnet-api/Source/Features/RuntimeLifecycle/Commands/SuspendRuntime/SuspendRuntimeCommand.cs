using Source.Features.RuntimeLifecycle.Models;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimeLifecycle.Commands.SuspendRuntime;

/// <summary>
/// User-triggered suspend for an <see cref="RuntimeState.Online"/> runtime on a
/// branch. Walks the most-recent (non-deleted) runtime to
/// <see cref="RuntimeState.Suspending"/> and best-effort stops the Fly machine;
/// the webhook / reconciler closes <c>Suspending → Suspended</c>.
///
/// <para>Powers <c>POST /api/projects/{projectId}/branches/{branchId}/runtime/suspend</c>.
/// Returns the current <see cref="RuntimeStatusResponse"/> snapshot so the frontend
/// can immediately render <c>Suspending</c> without a follow-up GET.</para>
/// </summary>
public sealed record SuspendRuntimeCommand(
    Guid ProjectId,
    Guid BranchId,
    Guid UserId
) : ICommand<Result<RuntimeStatusResponse>>;

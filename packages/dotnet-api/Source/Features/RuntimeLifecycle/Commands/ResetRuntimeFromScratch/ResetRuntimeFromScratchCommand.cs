using Source.Features.RuntimeLifecycle.Models;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimeLifecycle.Commands.ResetRuntimeFromScratch;

/// <summary>
/// Wipes Fly machine + volume references and reprovisions from a clean disk.
/// Powers <c>POST /api/projects/{projectId}/branches/{branchId}/runtime/reset-from-scratch</c>.
/// </summary>
public sealed record ResetRuntimeFromScratchCommand(
    Guid ProjectId,
    Guid BranchId,
    Guid UserId
) : ICommand<Result<RuntimeStatusResponse>>;

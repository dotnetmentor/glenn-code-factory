using Source.Features.RuntimeLifecycle.Models;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimeLifecycle.Commands.ForceStopRuntime;

/// <summary>
/// User-triggered force-stop for a branch runtime — parks the Fly machine from
/// <see cref="RuntimeState.Online"/> or any mid-boot state. Mirrors the operator
/// <see cref="Controllers.RuntimeAdminController.ForceStop"/> edges but is
/// addressed by <c>(ProjectId, BranchId)</c> and gated to project owners.
/// </summary>
public sealed record ForceStopRuntimeCommand(
    Guid ProjectId,
    Guid BranchId,
    Guid UserId
) : ICommand<Result<RuntimeStatusResponse>>;

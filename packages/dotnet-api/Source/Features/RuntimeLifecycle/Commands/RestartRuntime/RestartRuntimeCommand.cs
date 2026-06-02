using Source.Features.RuntimeLifecycle.Models;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimeLifecycle.Commands.RestartRuntime;

/// <summary>
/// User-triggered restart for a branch runtime. Suspended runtimes wake;
/// Online, mid-boot, Failed, and Crashed runtimes hard-reboot on the existing
/// volume via <see cref="ProjectRuntime.Restart"/>.
///
/// <para>Powers <c>POST /api/projects/{projectId}/branches/{branchId}/runtime/restart</c>.
/// Returns the current <see cref="RuntimeStatusResponse"/> snapshot so the
/// frontend can immediately render the new <c>Pending</c> (or <c>Waking</c>)
/// state without a follow-up round trip — the SignalR runtime-state channel
/// takes it from there as Booting → Online proceeds.</para>
///
/// <para><b>Authorisation gate.</b> Resolved in the handler: missing runtime,
/// soft-deleted runtime and (eventually) non-member callers all collapse to
/// <see cref="RestartRuntimeHandler.NotFoundPrefix"/> so the controller can map
/// to 404 without leaking runtime existence. A wrong-state runtime (anything
/// other than Suspended, Failed, or Crashed) returns
/// <see cref="RestartRuntimeHandler.ConflictPrefix"/> so the controller maps
/// to 409 — the user can read the error and try the right tool (the page is
/// already rendering the live state).</para>
/// </summary>
public sealed record RestartRuntimeCommand(
    Guid ProjectId,
    Guid BranchId,
    Guid UserId
) : ICommand<Result<RuntimeStatusResponse>>;

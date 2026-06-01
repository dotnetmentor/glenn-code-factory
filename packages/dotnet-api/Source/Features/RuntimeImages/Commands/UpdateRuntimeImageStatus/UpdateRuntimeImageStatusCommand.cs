using Source.Features.RuntimeImages.Models;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimeImages.Commands.UpdateRuntimeImageStatus;

/// <summary>
/// Set a <see cref="RuntimeImage"/>'s <see cref="RuntimeImage.Status"/>. The operator's
/// activation flow flows through here: when transitioning a row to
/// <see cref="RuntimeImageStatus.Active"/> the handler enforces the
/// <em>single-Active invariant</em> by demoting every other currently-Active row to
/// <see cref="RuntimeImageStatus.Deprecated"/> in the same SaveChanges call, so the
/// rest of the system (e.g. <c>RuntimeProvisionerJob</c>, which still reads "newest
/// row with Status == Active") continues to operate on a single source of truth.
///
/// <para>Lives behind MediatR (rather than inline in the controller) precisely because
/// the multi-row state transition is the kind of business invariant that benefits from
/// a unit-testable handler — see <c>UpdateRuntimeImageStatusHandlerTests</c> for the
/// pinned behaviour.</para>
/// </summary>
public sealed record UpdateRuntimeImageStatusCommand(
    Guid Id,
    RuntimeImageStatus NewStatus) : ICommand<Result<RuntimeImage>>;

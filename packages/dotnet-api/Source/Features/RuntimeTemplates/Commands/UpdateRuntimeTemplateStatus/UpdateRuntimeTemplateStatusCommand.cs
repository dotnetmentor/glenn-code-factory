using Source.Features.RuntimeTemplates.Models;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimeTemplates.Commands.UpdateRuntimeTemplateStatus;

/// <summary>
/// Move a registered template box to a new lifecycle status. Promoting to
/// <see cref="RuntimeTemplateStatus.Active"/> demotes every other Active row —
/// the single-Active invariant <c>RuntimeProvisionerJob</c> relies on when
/// picking a fork source.
/// </summary>
public record UpdateRuntimeTemplateStatusCommand(Guid Id, RuntimeTemplateStatus NewStatus)
    : ICommand<Result<RuntimeTemplate>>;

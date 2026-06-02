using Source.Features.RuntimeImages.Models;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimeImages.Queries.GetActiveRuntimeImage;

/// <summary>
/// The newest <see cref="RuntimeImageStatus.Active"/> runtime base image, if any.
/// </summary>
public sealed record GetActiveRuntimeImageQuery() : IQuery<Result<RuntimeImage?>>;

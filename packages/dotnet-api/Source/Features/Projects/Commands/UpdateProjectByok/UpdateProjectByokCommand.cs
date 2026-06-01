using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Commands.UpdateProjectByok;

public readonly record struct OptionalSecret(bool IsSet, string? Value)
{
    public static OptionalSecret Unchanged() => new(IsSet: false, Value: null);
    public static OptionalSecret Clear() => new(IsSet: true, Value: null);
    public static OptionalSecret SetTo(string plaintext) => new(IsSet: true, Value: plaintext);
}

public sealed record UpdateProjectByokCommand(
    Guid ProjectId,
    string CallingUserId,
    OptionalSecret CursorApiKey
) : ICommand<Result<UpdateProjectByokResponse>>;

public sealed record UpdateProjectByokResponse(
    Guid ProjectId,
    bool HasCursorApiKey);

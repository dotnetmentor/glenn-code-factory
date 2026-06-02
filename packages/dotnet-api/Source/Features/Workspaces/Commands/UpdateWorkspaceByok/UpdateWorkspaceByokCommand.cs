using Source.Features.Projects.Commands.UpdateProjectByok;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Workspaces.Commands.UpdateWorkspaceByok;

public sealed record UpdateWorkspaceByokCommand(
    OptionalSecret CursorApiKey,
    bool SetAllowProjectCursorApiKeyOverride,
    bool? AllowProjectCursorApiKeyOverride
) : ICommand<Result<UpdateWorkspaceByokResponse>>;

public sealed record UpdateWorkspaceByokResponse(
    Guid WorkspaceId,
    bool HasCursorApiKey,
    bool AllowProjectCursorApiKeyOverride);

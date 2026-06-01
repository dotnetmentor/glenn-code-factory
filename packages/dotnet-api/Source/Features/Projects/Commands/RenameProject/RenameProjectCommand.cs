using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Commands.RenameProject;

/// <summary>
/// Partial-update of a project's mutable settings. Powers
/// <c>PATCH /api/projects/{projectId}</c>. Every field on the command is
/// optional — null means "leave alone". The repo coordinates
/// (<c>GithubRepoOwner</c>/<c>GithubRepoName</c>) are independent and not
/// touched here.
///
/// <para>Currently supported fields:</para>
/// <list type="bullet">
///   <item><see cref="Name"/> — display name. Validation on
///         <c>Project.Rename(string)</c> (<c>name_required</c> /
///         <c>name_too_long</c>).</item>
///   <item><see cref="PreviewPort"/> — cloudflare-tunnel-preview Phase 2
///         per-project dev-server port. Validation on
///         <c>Project.SetPreviewPort(int)</c> (<c>invalid_preview_port</c>).</item>
/// </list>
///
/// <para><b>Authorisation gate.</b> Caller must be a workspace member with
/// Admin role or higher (Owner ⊇ Admin). Missing/soft-deleted project,
/// non-member caller and member-without-admin all collapse to the
/// <see cref="RenameProjectHandler.NotFoundPrefix"/> sentinel so the controller
/// can return 404 without leaking either project existence or the caller's
/// exact privilege gap.</para>
///
/// <para>No response payload — the controller returns 204. If the frontend
/// needs the updated row it can re-fetch via <c>GET /api/projects/{projectId}</c>.</para>
/// </summary>
public sealed record RenameProjectCommand(
    Guid ProjectId,
    string CallerUserId,
    string? Name = null,
    int? PreviewPort = null
) : ICommand<Result>;

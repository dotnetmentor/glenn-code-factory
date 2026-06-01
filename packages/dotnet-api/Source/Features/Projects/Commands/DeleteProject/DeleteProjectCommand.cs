using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Commands.DeleteProject;

/// <summary>
/// Soft-delete a project. The row stays in the database with
/// <c>IsDeleted=true</c> + <c>DeletedAt</c> / <c>DeletedBy</c> stamped by the
/// DbContext, and is filtered out of every subsequent query by the global
/// <c>!IsDeleted</c> query filter. Restoration is a separate flow (not
/// included on this card). Powers <c>DELETE /api/projects/{projectId}</c>.
///
/// <para><b>Authorisation gate.</b> Caller must be a workspace member with
/// Admin role or higher (Owner ⊇ Admin). Missing/soft-deleted project,
/// non-member caller and member-without-admin all collapse to the
/// <see cref="DeleteProjectHandler.NotFoundPrefix"/> sentinel so the controller
/// can return 404 without leaking either project existence or the caller's
/// exact privilege gap.</para>
///
/// <para><b>Idempotent.</b> Soft-deleting an already-deleted project is a
/// success no-op rather than a 404 — concurrent "Delete" clicks from two tabs
/// shouldn't 404 the second one. (In practice the global query filter means
/// the second call won't even find the row to delete, which already collapses
/// to the not-found path; this is documented intent.)</para>
///
/// <para>No response payload — the controller returns 204. Side-effects (stop
/// runtimes for deleted projects) are owned by downstream handlers reacting to
/// the <c>ProjectDeleted</c> event in follow-up work.</para>
/// </summary>
public sealed record DeleteProjectCommand(
    Guid ProjectId,
    string CallerUserId
) : ICommand<Result>;

namespace Source.Features.Projects.Commands.CreateProject;

using Source.Features.Projects.Models;

/// <summary>
/// Discriminated outcome of <see cref="CreateProjectCommand"/>: a successful create returns
/// the new <see cref="ProjectDto"/> in <see cref="Project"/>; a "this repo already belongs to
/// a project in this workspace" rejection returns the offending project's identity in
/// <see cref="Conflict"/>.
///
/// <para>We keep the success path as a typed <see cref="ProjectDto"/> rather than collapsing
/// onto a generic Result error code so the existing <c>Result&lt;ProjectDto&gt;</c> consumers
/// migrate with a one-line shape change. The conflict payload carries the existing project's
/// id + name so the frontend can render "Open existing project ABC" without a second
/// round-trip — that's the whole point of returning the duplicate inline rather than as a
/// generic 409 string.</para>
///
/// <para>Exactly one of <see cref="Project"/> / <see cref="Conflict"/> is non-null on a
/// successful <see cref="Source.Shared.Results.Result"/>; this is a poor-man's discriminated
/// union (records can't be union-typed in C# yet). Failed Results use the existing string
/// error channel — sentinel-prefixed for membership / pool-empty as today.</para>
/// </summary>
public sealed record CreateProjectOutcome(
    ProjectDto? Project,
    RepositoryAlreadyLinkedConflict? Conflict);

/// <summary>
/// Wire shape for the "RepositoryAlreadyLinked" 409 body. Carries enough state for the
/// frontend to render an actionable "Open existing project" affordance without re-fetching:
/// <list type="bullet">
///   <item><see cref="Code"/> — stable machine-readable identifier (<c>RepositoryAlreadyLinked</c>).
///         Surfaced to make the frontend's switch-on-error code uniform with the existing
///         <c>BranchAlreadyLinked</c> shape.</item>
///   <item><see cref="Message"/> — human sentence ready to drop into a toast / banner.</item>
///   <item><see cref="ExistingProjectId"/> / <see cref="ExistingProjectName"/> — identity of
///         the project that already links the repo. The frontend uses the id to route
///         straight to the project page on the "Open it instead" click.</item>
/// </list>
/// </summary>
public sealed record RepositoryAlreadyLinkedConflict(
    string Code,
    string Message,
    Guid ExistingProjectId,
    string ExistingProjectName);

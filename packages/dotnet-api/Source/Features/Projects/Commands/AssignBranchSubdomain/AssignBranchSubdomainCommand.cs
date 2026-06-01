using Source.Features.Projects.Models;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Commands.AssignBranchSubdomain;

/// <summary>
/// Claim a fresh preview subdomain from the Cloudflare pool and bind it to an
/// existing branch that doesn't have one yet. The user-facing recovery path
/// for legacy branches that pre-date the cloudflare-tunnel-preview Phase 3
/// pool (their <c>AssignedSubdomain</c> is <c>null</c>, so
/// <c>ProjectBranchDto.PreviewHostname</c> is <c>null</c>, so the Preview tab
/// renders the empty "No preview subdomain yet" state).
///
/// <para>Thin wrapper around
/// <c>Source.Features.Cloudflare.Commands.AssignSubdomainToBranchCommand</c> —
/// this command adds the project-scoped membership + branch-existence checks
/// the inner command does NOT do, and returns the freshly-shaped
/// <see cref="ProjectBranchDto"/> so the frontend can drop the result straight
/// into its branches cache.</para>
///
/// <para><b>Failure modes (controller maps these to HTTP):</b>
/// <list type="bullet">
///   <item><see cref="AssignBranchSubdomainHandler.NotFoundError"/> (404) —
///         project or branch missing, or caller isn't a member of the
///         workspace. Existence-safe.</item>
///   <item><see cref="AssignBranchSubdomainHandler.AlreadyAssignedError"/>
///         (409) — branch already has a subdomain. Idempotent re-call from
///         the frontend lands here; the controller surfaces the existing
///         hostname.</item>
///   <item><see cref="AssignBranchSubdomainHandler.PoolEmptyError"/> (409) —
///         pool is exhausted. Frontend shows the spec's "ask your admin to
///         batch-create more" message.</item>
/// </list>
/// </para>
/// </summary>
public sealed record AssignBranchSubdomainCommand(
    string CallerUserId,
    Guid ProjectId,
    Guid BranchId
) : ICommand<Result<ProjectBranchDto>>;

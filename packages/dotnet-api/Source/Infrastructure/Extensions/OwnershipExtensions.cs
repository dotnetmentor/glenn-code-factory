using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Source.Features.Conversations.Models;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure.AuthorizationModels;

namespace Source.Infrastructure.Extensions;

/// <summary>
/// Project-ownership gating helpers — the shared implementation of the
/// "caller must own the parent project" check that <see cref="Source.Features.Diffs.Controllers.DiffsController"/>
/// established as the codebase convention.
///
/// <para><b>Contract.</b> Each method returns <c>null</c> (or <c>false</c>)
/// on BOTH "the resource doesn't exist" AND "the caller doesn't own it".
/// Controllers must surface a uniform <c>404</c> in both cases — returning
/// <c>403</c> on the second case would confirm that the resource id exists,
/// leaking cross-tenant existence information to a probing client. Logging
/// the ownership-mismatch case at <c>Information</c> is fine and encouraged
/// (it's a curious-but-harmless probe), but the response shape MUST be
/// indistinguishable from "doesn't exist".</para>
///
/// <para><b>Why <c>AsNoTracking</c>.</b> Every caller is an HTTP read path
/// that does not mutate the loaded entity — the gate is a lookup, not the
/// start of a unit-of-work. Tracking would be wasted change-detection cost.
/// Handlers that DO need to mutate the entity should re-load it tracked
/// inside their own scope; this helper is for the controller boundary.</para>
///
/// <para><b>Why <c>StringComparison.Ordinal</c>.</b> ASP.NET Core Identity
/// user keys are GUIDs serialised as lowercase strings, but treating them as
/// opaque tokens (byte-exact comparison) is the safer default — it matches
/// what EF Core does internally for string PK lookups and what
/// <see cref="DiffsController"/> uses today.</para>
///
/// <para><b>Defensive null-guard on user id.</b> All four methods return
/// <c>null</c>/<c>false</c> when <see cref="ClaimsPrincipalExtensions.GetUserId"/>
/// returns null/empty. <c>[Authorize]</c> on the controller should make this
/// unreachable, but a controller without a NameIdentifier claim is a bug —
/// not a 404 leak — and we'd rather return 404 than NRE.</para>
///
/// <para>See <see cref="Source.Features.Diffs.Controllers.DiffsController"/>
/// for the established convention.</para>
/// </summary>
public static class OwnershipExtensions
{
    /// <summary>
    /// Verify the caller owns the project identified by <paramref name="projectId"/>.
    /// Returns <c>true</c> only when the project both exists AND its
    /// <c>OwnerUserId</c> matches the caller's <c>NameIdentifier</c> claim.
    /// Returns <c>false</c> on either gap — caller MUST surface a uniform
    /// <c>404</c> (never 403) so cross-tenant existence is not leaked.
    /// </summary>
    public static async Task<bool> CallerOwnsProjectAsync(
        this ApplicationDbContext db,
        ClaimsPrincipal user,
        Guid projectId,
        CancellationToken ct)
    {
        var callerUserId = user.GetUserId();
        if (string.IsNullOrEmpty(callerUserId))
        {
            return false;
        }

        var ownerUserId = await db.Projects
            .AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => p.OwnerUserId)
            .FirstOrDefaultAsync(ct);

        if (ownerUserId is null)
        {
            // Project doesn't exist (or is filtered out by a global query
            // filter such as soft-delete). Indistinguishable from "not yours".
            return false;
        }

        return string.Equals(ownerUserId, callerUserId, StringComparison.Ordinal);
    }

    /// <summary>
    /// Load a <see cref="ProjectRuntime"/> by id with its <see cref="ProjectRuntime.Project"/>
    /// nav, and verify the caller owns that project. Returns <c>null</c> when
    /// either the runtime doesn't exist or the caller isn't the project's
    /// owner. Caller MUST surface a uniform <c>404</c> on null (never 403).
    /// </summary>
    public static async Task<ProjectRuntime?> ResolveOwnedRuntimeAsync(
        this ApplicationDbContext db,
        ClaimsPrincipal user,
        Guid runtimeId,
        CancellationToken ct)
    {
        var callerUserId = user.GetUserId();
        if (string.IsNullOrEmpty(callerUserId))
        {
            return null;
        }

        var runtime = await db.ProjectRuntimes
            .AsNoTracking()
            .Include(r => r.Project)
            .FirstOrDefaultAsync(r => r.Id == runtimeId, ct);

        if (runtime is null)
        {
            return null;
        }

        if (!string.Equals(runtime.Project.OwnerUserId, callerUserId, StringComparison.Ordinal))
        {
            return null;
        }

        return runtime;
    }

    /// <summary>
    /// Load a <see cref="Conversation"/> by id and verify the caller owns the
    /// project the conversation hangs off (resolved through
    /// <see cref="Conversation.ProjectId"/>). Returns <c>null</c> on either
    /// "doesn't exist" or "not yours". Caller MUST surface a uniform <c>404</c>
    /// on null (never 403).
    /// </summary>
    public static async Task<Conversation?> ResolveOwnedConversationAsync(
        this ApplicationDbContext db,
        ClaimsPrincipal user,
        Guid conversationId,
        CancellationToken ct)
    {
        var callerUserId = user.GetUserId();
        if (string.IsNullOrEmpty(callerUserId))
        {
            return null;
        }

        // Conversation.ProjectId is a plain Guid (no FK / no nav) so we
        // resolve ownership via a join through the Projects table rather than
        // an Include. Single round-trip, two index lookups.
        var row = await db.Conversations
            .AsNoTracking()
            .Where(c => c.Id == conversationId)
            .Select(c => new
            {
                Conversation = c,
                OwnerUserId = db.Projects
                    .Where(p => p.Id == c.ProjectId)
                    .Select(p => p.OwnerUserId)
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(ct);

        if (row is null || row.OwnerUserId is null)
        {
            return null;
        }

        if (!string.Equals(row.OwnerUserId, callerUserId, StringComparison.Ordinal))
        {
            return null;
        }

        return row.Conversation;
    }

    /// <summary>
    /// Load an <see cref="AgentSession"/> by id with its
    /// <see cref="AgentSession.Conversation"/> nav, and verify the caller
    /// owns the project the conversation hangs off. Returns <c>null</c> on
    /// either gap. Caller MUST surface a uniform <c>404</c> on null
    /// (never 403).
    /// </summary>
    public static async Task<AgentSession?> ResolveOwnedSessionAsync(
        this ApplicationDbContext db,
        ClaimsPrincipal user,
        Guid sessionId,
        CancellationToken ct)
    {
        var callerUserId = user.GetUserId();
        if (string.IsNullOrEmpty(callerUserId))
        {
            return null;
        }

        // Conversation.ProjectId is a plain Guid (no Project nav), so we
        // can't traverse session → conversation → project via Include. Pull
        // the session + its conversation, then resolve OwnerUserId via a
        // single sub-select against Projects.
        var row = await db.AgentSessions
            .AsNoTracking()
            .Include(s => s.Conversation)
            .Where(s => s.Id == sessionId)
            .Select(s => new
            {
                Session = s,
                OwnerUserId = db.Projects
                    .Where(p => p.Id == s.Conversation.ProjectId)
                    .Select(p => p.OwnerUserId)
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(ct);

        if (row is null || row.OwnerUserId is null)
        {
            return null;
        }

        if (!string.Equals(row.OwnerUserId, callerUserId, StringComparison.Ordinal))
        {
            return null;
        }

        return row.Session;
    }

    /// <summary>
    /// Read-side access gate for project-scoped observability endpoints
    /// (runtime status, apply history, runtime events). Returns <c>true</c>
    /// when the caller is ANY of:
    /// <list type="bullet">
    ///   <item>a <see cref="RoleConstants.SuperAdmin"/> (platform-wide bypass,
    ///         matching the convention used by
    ///         <see cref="Source.Infrastructure.Workspaces.RequireWorkspaceRoleAttribute"/>);</item>
    ///   <item>the project's <c>OwnerUserId</c>;</item>
    ///   <item>a <c>WorkspaceMembership</c> row for the workspace the project
    ///         belongs to (any role — Owner, Admin, Member all suffice for read).</item>
    /// </list>
    ///
    /// <para><b>Why this exists alongside <see cref="CallerOwnsProjectAsync"/>.</b>
    /// The strict owner-only gate remains the right call for write/state-change
    /// endpoints (restart, decision, etc.). Read-only observability surfaces
    /// (runtime status header, apply history, timeline) need to be visible to
    /// every workspace member so the in-workspace debug panel works for non-owner
    /// teammates — see <c>workspace-runtime-observability</c> spec, Section E.</para>
    ///
    /// <para><b>Response shape on failure.</b> Returns <c>false</c> on both
    /// "project doesn't exist" AND "exists but caller has no access". Controllers
    /// MUST surface a uniform <c>404</c> — same anti-leak convention as
    /// <see cref="CallerOwnsProjectAsync"/>.</para>
    /// </summary>
    public static async Task<bool> CallerCanAccessProjectAsync(
        this ApplicationDbContext db,
        ClaimsPrincipal user,
        Guid projectId,
        CancellationToken ct)
    {
        return await db.UserCanAccessProjectAsync(
            user.GetUserId(),
            user.IsInRole(RoleConstants.SuperAdmin),
            projectId,
            ct);
    }

    /// <summary>
    /// Project access gate keyed by user id + SuperAdmin flag — for MediatR handlers
    /// that receive <c>CallerUserId</c> from the controller instead of a
    /// <see cref="ClaimsPrincipal"/>.
    /// </summary>
    public static async Task<bool> UserCanAccessProjectAsync(
        this ApplicationDbContext db,
        string? callerUserId,
        bool callerIsSuperAdmin,
        Guid projectId,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(callerUserId))
        {
            return false;
        }

        if (callerIsSuperAdmin)
        {
            return await db.Projects
                .AsNoTracking()
                .AnyAsync(p => p.Id == projectId, ct);
        }

        var row = await db.Projects
            .AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => new { p.OwnerUserId, p.WorkspaceId })
            .FirstOrDefaultAsync(ct);

        if (row is null)
        {
            return false;
        }

        if (string.Equals(row.OwnerUserId, callerUserId, StringComparison.Ordinal))
        {
            return true;
        }

        return await db.WorkspaceMemberships
            .AsNoTracking()
            .AnyAsync(
                m => m.WorkspaceId == row.WorkspaceId && m.UserId == callerUserId,
                ct);
    }

    /// <summary>
    /// Returns the parent project's id for a conversation, or null when the row is missing.
    /// </summary>
    public static async Task<Guid?> FindProjectIdForConversationAsync(
        this ApplicationDbContext db,
        Guid conversationId,
        CancellationToken ct)
    {
        return await db.Conversations
            .AsNoTracking()
            .Where(c => c.Id == conversationId)
            .Select(c => (Guid?)c.ProjectId)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Read-side access gate for project-scoped observability endpoints
    /// (e.g. <c>/api/runtime-events</c>, the SignalR <c>runtime-events:{id}</c>
    /// group join). Returns the loaded <see cref="ProjectRuntime"/> (with its
    /// <see cref="ProjectRuntime.Project"/> nav) when the caller is ANY of:
    /// SuperAdmin · project owner · workspace member of the runtime's owning
    /// workspace. Returns <c>null</c> on either gap. Caller MUST surface a
    /// uniform <c>404</c> on null (never 403) — anti-leak convention.
    ///
    /// <para>Strict-owner-only counterpart: <see cref="ResolveOwnedRuntimeAsync"/>.
    /// Use that for write paths; use this one for read-only observability.</para>
    /// </summary>
    public static async Task<ProjectRuntime?> ResolveAccessibleRuntimeAsync(
        this ApplicationDbContext db,
        ClaimsPrincipal user,
        Guid runtimeId,
        CancellationToken ct)
    {
        var callerUserId = user.GetUserId();
        if (string.IsNullOrEmpty(callerUserId))
        {
            return null;
        }

        var runtime = await db.ProjectRuntimes
            .AsNoTracking()
            .Include(r => r.Project)
            .FirstOrDefaultAsync(r => r.Id == runtimeId, ct);

        if (runtime is null)
        {
            return null;
        }

        if (user.IsInRole(RoleConstants.SuperAdmin))
        {
            return runtime;
        }

        if (string.Equals(runtime.Project.OwnerUserId, callerUserId, StringComparison.Ordinal))
        {
            return runtime;
        }

        var isMember = await db.WorkspaceMemberships
            .AsNoTracking()
            .AnyAsync(
                m => m.WorkspaceId == runtime.Project.WorkspaceId && m.UserId == callerUserId,
                ct);

        return isMember ? runtime : null;
    }
}

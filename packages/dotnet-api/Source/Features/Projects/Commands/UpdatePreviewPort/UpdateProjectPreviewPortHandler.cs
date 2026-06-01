using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Source.Features.Cloudflare.Services;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Commands.UpdatePreviewPort;

/// <summary>
/// Handles <see cref="UpdateProjectPreviewPortCommand"/>. Three-phase pipeline:
///
/// <list type="number">
///   <item><b>DB write.</b> Load the project, delegate validation +
///         no-op-detection to <c>Project.SetPreviewPort(int)</c>, save.</item>
///   <item><b>Cloudflare fan-out.</b> Query every branch in the project that
///         has an assigned subdomain and re-PUT its ingress configuration
///         pointing at the new port. Bounded concurrency (5) so a project
///         with many branches doesn't smash Cloudflare with parallel PUTs.</item>
///   <item><b>SignalR push.</b> Broadcast a <see cref="PreviewPortChangedNotification"/>
///         to every branch group of the project AND to the workspace group
///         so any open settings view picks the new value up live.</item>
/// </list>
///
/// <para><b>Idempotency.</b> Calling with the current port is a true no-op
/// (the DB save short-circuits via <c>Project.SetPreviewPort</c>, the
/// Cloudflare fan-out is skipped, and we DO still emit the SignalR push
/// because two tabs racing the same change benefit from a confirmation
/// signal). Cloudflare's <c>PUT /configurations</c> is itself idempotent —
/// re-issuing with the same body replaces the ingress with itself.</para>
///
/// <para><b>Partial Cloudflare failure.</b> If one tunnel PUT throws we
/// catch, log with the tunnel + branch ids, and continue with the rest.
/// The DB row is already the source of truth — the next port change (or a
/// dedicated reconciler) catches the still-stale tunnel up. The response
/// carries the failure count so the caller can react if it cares.</para>
/// </summary>
public sealed class UpdateProjectPreviewPortHandler
    : ICommandHandler<UpdateProjectPreviewPortCommand, Result<UpdateProjectPreviewPortResponse>>
{
    /// <summary>
    /// Upper bound on parallel Cloudflare API calls. Cloudflare's API has its
    /// own rate-limit envelope; 5 is well below it and is plenty even for a
    /// project with dozens of branches — the PUT itself is fast (sub-second
    /// edge propagation) so latency dominates.
    /// </summary>
    private const int CloudflareFanOutConcurrency = 5;

    private readonly ApplicationDbContext _db;
    private readonly CloudflareApiClient _cloudflare;
    private readonly IHubContext<AgentHub, IAgentClient> _hub;
    private readonly ILogger<UpdateProjectPreviewPortHandler> _logger;

    public UpdateProjectPreviewPortHandler(
        ApplicationDbContext db,
        CloudflareApiClient cloudflare,
        IHubContext<AgentHub, IAgentClient> hub,
        ILogger<UpdateProjectPreviewPortHandler> logger)
    {
        _db = db;
        _cloudflare = cloudflare;
        _hub = hub;
        _logger = logger;
    }

    public async Task<Result<UpdateProjectPreviewPortResponse>> Handle(
        UpdateProjectPreviewPortCommand request,
        CancellationToken cancellationToken)
    {
        // Tracked load — SetPreviewPort mutates. Global IsDeleted filter
        // already excludes soft-deleted rows.
        var project = await _db.Projects
            .FirstOrDefaultAsync(p => p.Id == request.ProjectId, cancellationToken);

        if (project is null)
        {
            return Result.Failure<UpdateProjectPreviewPortResponse>("Project not found");
        }

        // Capture BEFORE the rich method runs so we can detect the no-op path
        // for the Cloudflare fan-out. The rich method already returns Success
        // without mutating when the port matches, but we want to skip the
        // entire fan-out (not just the save) in that case — the tunnels
        // already point at the right port.
        var portUnchanged = project.PreviewPort == request.Port;

        var setResult = project.SetPreviewPort(request.Port);
        if (setResult.IsFailure)
        {
            return Result.Failure<UpdateProjectPreviewPortResponse>(setResult.Error!);
        }

        if (portUnchanged)
        {
            // No DB write needed, no Cloudflare PUTs needed. We still emit
            // the SignalR push so other tabs that just saved "the same value"
            // see a confirmation and re-sync their local state.
            await BroadcastAsync(project.WorkspaceId, request.ProjectId, request.Port, cancellationToken);
            _logger.LogInformation(
                "Preview port for project {ProjectId} unchanged at {Port}; skipped Cloudflare fan-out.",
                request.ProjectId, request.Port);
            return Result.Success(new UpdateProjectPreviewPortResponse(
                request.ProjectId, request.Port, TunnelsUpdated: 0, TunnelsFailed: 0));
        }

        await _db.SaveChangesAsync(cancellationToken);

        // Pull every branch with an assigned subdomain in a single query. Not
        // tracked — we only need the read-only triple (branchId, tunnelId,
        // hostname) to drive the Cloudflare PUT.
        var targets = await _db.ProjectBranches
            .AsNoTracking()
            .Where(b => b.ProjectId == request.ProjectId && b.AssignedSubdomain != null)
            .Select(b => new BranchTunnelTarget(
                b.Id,
                b.AssignedSubdomain!.TunnelId,
                b.AssignedSubdomain!.Hostname))
            .ToListAsync(cancellationToken);

        var (updated, failed) = await FanOutCloudflareAsync(
            targets, request.Port, cancellationToken);

        await BroadcastAsync(project.WorkspaceId, request.ProjectId, request.Port, cancellationToken);

        _logger.LogInformation(
            "Updated preview port for project {ProjectId} to {Port}: {Updated} tunnels updated, {Failed} failed.",
            request.ProjectId, request.Port, updated, failed);

        return Result.Success(new UpdateProjectPreviewPortResponse(
            request.ProjectId, request.Port, updated, failed));
    }

    /// <summary>
    /// Bounded-concurrency fan-out of the Cloudflare ingress PUTs. Each
    /// failure is caught and logged; the loop continues so a single broken
    /// tunnel doesn't block the rest. Returns (success count, failure count).
    /// </summary>
    private async Task<(int Updated, int Failed)> FanOutCloudflareAsync(
        IReadOnlyList<BranchTunnelTarget> targets,
        int port,
        CancellationToken ct)
    {
        if (targets.Count == 0)
        {
            return (0, 0);
        }

        using var sem = new SemaphoreSlim(CloudflareFanOutConcurrency);
        var tasks = targets.Select(async target =>
        {
            await sem.WaitAsync(ct);
            try
            {
                await _cloudflare.AddPublicHostnameAsync(
                    target.TunnelId, target.Hostname, port, ct);
                return true;
            }
            catch (Exception ex)
            {
                // Don't poison the fan-out — log + count as failure. The DB
                // row is the source of truth; next port change picks up the
                // missing tunnels.
                _logger.LogError(ex,
                    "Cloudflare ingress update failed for branch {BranchId} tunnel {TunnelId} at port {Port}.",
                    target.BranchId, target.TunnelId, port);
                return false;
            }
            finally
            {
                sem.Release();
            }
        });

        var results = await Task.WhenAll(tasks);
        var updated = results.Count(r => r);
        var failed = results.Length - updated;
        return (updated, failed);
    }

    /// <summary>
    /// Fan the realtime notification out to both the per-branch groups (where
    /// chat/runtime tabs live — they join <c>branch-{branchId}</c> on
    /// connect) AND the workspace group (where the sidebar / settings views
    /// live — they join <c>workspace-{workspaceId}</c> via <c>JoinWorkspace</c>).
    /// SignalR delivery failures are swallowed — the DB + Cloudflare side
    /// effects already happened, so the push is best-effort UX.
    /// </summary>
    private async Task BroadcastAsync(
        Guid workspaceId,
        Guid projectId,
        int port,
        CancellationToken ct)
    {
        var payload = new PreviewPortChangedNotification(
            ProjectId: projectId,
            Port: port,
            OccurredAt: DateTime.UtcNow);

        try
        {
            await _hub.Clients.Group($"workspace-{workspaceId}").PreviewPortChanged(payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to broadcast PreviewPortChanged to workspace group for project {ProjectId}; DB + tunnels are already updated.",
                projectId);
        }

        // Also push to every branch group of the project so per-branch tabs
        // (which don't subscribe to the workspace group) pick the change up
        // without a refetch. One Group call per branch — cheap; SignalR
        // already does the multi-connection fan-out behind the group.
        List<Guid> branchIds;
        try
        {
            branchIds = await _db.ProjectBranches
                .AsNoTracking()
                .Where(b => b.ProjectId == projectId)
                .Select(b => b.Id)
                .ToListAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to list branches for PreviewPortChanged broadcast on project {ProjectId}; workspace push still went through.",
                projectId);
            return;
        }

        foreach (var branchId in branchIds)
        {
            try
            {
                await _hub.Clients.Group($"branch-{branchId}").PreviewPortChanged(payload);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to broadcast PreviewPortChanged to branch-{BranchId} group; other groups already received the push.",
                    branchId);
            }
        }
    }

    /// <summary>
    /// Lean projection for the Cloudflare fan-out — just the three fields a
    /// PUT call needs, kept as a private record so the LINQ projection is
    /// EF-translatable and the fan-out signature stays tidy.
    /// </summary>
    private sealed record BranchTunnelTarget(Guid BranchId, string TunnelId, string Hostname);
}

using Microsoft.EntityFrameworkCore;
using Source.Features.Cloudflare.Models;
using Source.Features.Cloudflare.Services;
using Source.Features.Projects.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Cloudflare.Commands;

/// <summary>
/// Atomically claim one <see cref="SubdomainStatus.Available"/> row from the
/// pool and bind it to <paramref name="BranchId"/>. Race-safe: uses Postgres'
/// <c>FOR UPDATE SKIP LOCKED</c> so two concurrent branch creations cannot
/// claim the same row — one gets it, the other moves on to the next available
/// row (or fails if the pool is exhausted).
///
/// <para><b>Failure modes:</b>
/// <list type="bullet">
///   <item><c>pool_empty</c> — no <see cref="SubdomainStatus.Available"/> rows
///         left. Phase 3 will surface this to the branch-create handler so it
///         fails loudly with the spec's "ask your admin to batch-create more"
///         error.</item>
/// </list>
/// </para>
///
/// <para><b>Phase 1 contract.</b> This handler is defined and callable but
/// NOT yet invoked from branch creation — that wiring is explicitly Phase 3.
/// It exists now so the pool is end-to-end testable in isolation.</para>
/// </summary>
public record AssignSubdomainToBranchCommand(Guid BranchId)
    : ICommand<Result<SubdomainAssignmentDto>>;

public class AssignSubdomainToBranchHandler
    : ICommandHandler<AssignSubdomainToBranchCommand, Result<SubdomainAssignmentDto>>
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<AssignSubdomainToBranchHandler> _logger;
    private readonly CloudflareApiClient? _cloudflare;

    public AssignSubdomainToBranchHandler(
        ApplicationDbContext db,
        ILogger<AssignSubdomainToBranchHandler> logger,
        CloudflareApiClient? cloudflare = null)
    {
        _db = db;
        _logger = logger;
        // Cloudflare client is optional so the existing unit-test surface
        // (HandlerTestBase + InMemory + NullLogger, no DI container) keeps
        // working without having to stand up a typed HttpClient. In
        // production it's always resolved by the container.
        _cloudflare = cloudflare;
    }

    public async Task<Result<SubdomainAssignmentDto>> Handle(
        AssignSubdomainToBranchCommand request,
        CancellationToken cancellationToken)
    {
        // Composition rule: if the caller (Phase 3: branch creation handlers)
        // has already opened a transaction, we participate in it — the claim
        // SaveChanges is deferred to the outer commit so "branch row +
        // subdomain claim" land atomically or both roll back. If we're invoked
        // standalone (no ambient tx), we open one ourselves so the
        // SELECT...FOR UPDATE lock survives the UPDATE on the same row.
        //
        // The same handler now serves both call paths cleanly; the outer
        // CreateProjectHandler doesn't have to know about the FOR UPDATE
        // SKIP LOCKED SQL, and standalone callers (tests, future admin tools)
        // still get correct atomic behaviour.
        //
        // InMemory provider note: the in-memory store throws on
        // BeginTransactionAsync — its transaction manager is a no-op stub.
        // Treat InMemory like an ambient-tx world (skip the BeginTransaction
        // and SaveChanges flush below); standalone tests still see the
        // mutated row because Add/Update on tracked entities are visible
        // to subsequent reads even without SaveChanges in InMemory.
        // Actually we still SaveChanges below in the InMemory case so
        // FindAsync round-trips see the changes — the path collapses to
        // "no explicit transaction, do SaveChanges inline".
        var isInMemory = string.Equals(
            _db.Database.ProviderName,
            "Microsoft.EntityFrameworkCore.InMemory",
            StringComparison.Ordinal);

        var ambientTx = _db.Database.CurrentTransaction;

        // Execution-strategy rule: Npgsql's retrying strategy forbids user-
        // initiated transactions because it can't retry across a
        // BeginTransaction/Commit boundary the caller owns. The strategy
        // requires the whole tx body to be wrapped in ExecuteAsync so it can
        // re-run the entire block on a transient failure.
        //
        // Three code paths converge here:
        //   1. Ambient tx (caller already owns one — CreateProject / CopyBranch):
        //      we MUST NOT wrap with our own strategy because strategies cannot
        //      nest. Caller has already taken ownership of retry semantics. We
        //      just participate (track the mutation, defer SaveChanges).
        //   2. InMemory (tests): provider supports neither transactions nor
        //      strategies — both are no-ops. Skip both entirely; SaveChanges
        //      inline so subsequent reads see the mutation.
        //   3. Standalone Postgres: wrap with execution strategy + open our own
        //      tx inside it. The strategy controls retry; on transient failure
        //      the whole claim re-runs (including the FOR UPDATE SKIP LOCKED).
        SubdomainAssignment? claimedRow = null;

        if (ambientTx is not null || isInMemory)
        {
            // Path 1 (ambient) + Path 2 (InMemory): no new transaction, no
            // strategy wrap. Run the claim inline.
            var inlineResult = await ClaimRowAsync(request.BranchId, cancellationToken);
            if (inlineResult is null)
            {
                _logger.LogWarning(
                    "AssignSubdomainToBranch failed for branch {BranchId}: pool_empty",
                    request.BranchId);
                return Result.Failure<SubdomainAssignmentDto>("pool_empty");
            }

            claimedRow = inlineResult;

            if (ambientTx is null)
            {
                // InMemory standalone — flush so subsequent reads see the
                // mutation. Ambient-tx path leaves SaveChanges to the outer
                // handler, which flushes our tracked mutation alongside its
                // own inside the outer transaction.
                await _db.SaveChangesAsync(cancellationToken);
            }
        }
        else
        {
            // Path 3: Postgres standalone. Strategy controls retry; tx body
            // runs inside ExecuteAsync. We must also signal "pool_empty" out
            // of the strategy block without committing — return null from the
            // operation and let the caller branch on it.
            var strategy = _db.Database.CreateExecutionStrategy();
            var branchId = request.BranchId;
            claimedRow = await strategy.ExecuteAsync<SubdomainAssignment?>(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
                var row = await ClaimRowAsync(branchId, cancellationToken);
                if (row is null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return null;
                }

                await _db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                return row;
            });

            if (claimedRow is null)
            {
                _logger.LogWarning(
                    "AssignSubdomainToBranch failed for branch {BranchId}: pool_empty",
                    request.BranchId);
                return Result.Failure<SubdomainAssignmentDto>("pool_empty");
            }
        }

        _logger.LogInformation(
            "Subdomain {Hostname} claimed by branch {BranchId}",
            claimedRow.Hostname, request.BranchId);

        // Reconcile Cloudflare-side tunnel ingress to the project's PreviewPort.
        //
        // Why here, and why best-effort:
        // Pool rows are pre-created with placeholder service=http://localhost:5173
        // (BatchCreateSubdomains.PlaceholderPreviewPort) so the tunnel + DNS
        // exist and validate before any branch claims them. The moment a branch
        // claims a row, we know which project it belongs to — and therefore
        // which PreviewPort cloudflared should route to. Pushing the PUT here
        // keeps the placeholder lie short-lived (claim → seconds later → first
        // request hits a correctly-routed tunnel) and means the existing
        // UpdateProjectPreviewPort fan-out has fewer drifted rows to chase.
        //
        // Best-effort because the claim itself is the contract this handler
        // owes its callers (CreateProject / CopyBranch / ForkBranchFromGit /
        // AttachGitBranch). A Cloudflare 5xx during ingress PUT must NOT roll
        // back the claim — the row is already locked + flipped to Assigned in
        // the same transaction as the caller's branch row, and unwinding that
        // would leave the pool in a worse state than a drifted ingress.
        // RuntimeProvisionerJob has a defensive PUT just before machine boot
        // that catches drifted rows.
        await ReconcileCloudflareIngressAsync(claimedRow, request.BranchId, cancellationToken);

        return Result.Success(new SubdomainAssignmentDto
        {
            Id = claimedRow.Id,
            Hostname = claimedRow.Hostname,
            Subdomain = claimedRow.Subdomain,
            Status = claimedRow.Status,
            CreatedAt = claimedRow.CreatedAt,
            AssignedBranchId = claimedRow.AssignedBranchId,
            AssignedAt = claimedRow.AssignedAt,
        });
    }

    /// <summary>
    /// Issue the FOR UPDATE SKIP LOCKED claim (or its InMemory equivalent) and
    /// mutate the row in the tracker. Returns the mutated row, or <c>null</c>
    /// when the pool is empty. Does NOT SaveChanges or commit — caller decides
    /// when/how to flush depending on whether there's an ambient tx, an
    /// in-progress strategy, or an InMemory standalone test.
    /// </summary>
    private async Task<SubdomainAssignment?> ClaimRowAsync(Guid branchId, CancellationToken cancellationToken)
    {
        var isInMemory = string.Equals(
            _db.Database.ProviderName,
            "Microsoft.EntityFrameworkCore.InMemory",
            StringComparison.Ordinal);

        // FromSqlRaw lets us bolt on the lock hint. We deliberately bypass
        // the soft-delete query filter via `IgnoreQueryFilters()`-equivalent
        // logic in the WHERE clause: we restrict to IsDeleted = false in SQL
        // so the lock acquires only the live row we're actually about to
        // mutate. ORDER BY CreatedAt keeps FIFO semantics — the row that's
        // been in the pool longest gets handed out first.
        //
        // SKIP LOCKED is the magic: a concurrent claim against the same row
        // doesn't wait — it just skips and picks the next one. Combined with
        // LIMIT 1 we get a clean "next available" atomic claim.
        const string sql =
            @"SELECT * FROM ""SubdomainAssignments""
              WHERE ""Status"" = 'Available' AND ""IsDeleted"" = false
              ORDER BY ""CreatedAt"" ASC
              FOR UPDATE SKIP LOCKED
              LIMIT 1";

        List<SubdomainAssignment> locked;

        // EF Core's in-memory provider (used in tests) doesn't support
        // FromSqlRaw — there's no SQL engine behind it. Fall back to a plain
        // LINQ "next available" query for test parity. The branch is a clean
        // boundary: production uses Postgres → FOR UPDATE SKIP LOCKED is real;
        // tests use InMemory → the LINQ path is single-threaded so locking
        // semantics are moot anyway. ProviderName comparison avoids depending
        // on the InMemory package in the production csproj.
        if (isInMemory)
        {
            locked = await _db.SubdomainAssignments
                .Where(x => x.Status == SubdomainStatus.Available)
                .OrderBy(x => x.CreatedAt)
                .Take(1)
                .ToListAsync(cancellationToken);
        }
        else
        {
            locked = await _db.SubdomainAssignments
                .FromSqlRaw(sql)
                .AsTracking()
                .ToListAsync(cancellationToken);
        }

        if (locked.Count == 0)
        {
            return null;
        }

        var row = locked[0];
        row.Status = SubdomainStatus.Assigned;
        row.AssignedBranchId = branchId;
        row.AssignedAt = DateTime.UtcNow;
        return row;
    }

    /// <summary>
    /// Best-effort PUT to Cloudflare's tunnel configurations endpoint so the
    /// claimed row's tunnel routes to <c>http://localhost:{project.PreviewPort}</c>.
    ///
    /// <para>Skips silently when:
    /// <list type="bullet">
    ///   <item>The Cloudflare client wasn't injected (unit-test path — see
    ///         constructor comment).</item>
    ///   <item>The project's PreviewPort equals the pool's placeholder
    ///         (<see cref="Project.DefaultPreviewPort"/>) — the tunnel is
    ///         already pointing at the right port from pool creation.</item>
    /// </list>
    /// </para>
    ///
    /// <para>Exceptions are caught and logged at warning level — the claim has
    /// already succeeded by the time we get here and we don't want a transient
    /// Cloudflare hiccup to roll it back. The provisioner-time defensive PUT
    /// (and the explicit UpdateProjectPreviewPort fan-out) will reconcile any
    /// rows this attempt drops.</para>
    /// </summary>
    private async Task ReconcileCloudflareIngressAsync(
        SubdomainAssignment claimedRow,
        Guid branchId,
        CancellationToken cancellationToken)
    {
        if (_cloudflare is null)
        {
            return;
        }

        int previewPort;
        try
        {
            // Join branch → project so we get exactly the one int we need in
            // a single round-trip. No tracking — read-only.
            previewPort = await _db.ProjectBranches
                .AsNoTracking()
                .Where(b => b.Id == branchId)
                .Select(b => b.Project.PreviewPort)
                .FirstOrDefaultAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "AssignSubdomainToBranch: failed to load PreviewPort for branch {BranchId}; skipping Cloudflare ingress reconciliation. Tunnel {TunnelId} stays at pool placeholder until RuntimeProvisionerJob / UpdateProjectPreviewPort reconciles it.",
                branchId, claimedRow.TunnelId);
            return;
        }

        if (previewPort == 0)
        {
            // Defensive — caller passed a branch id that doesn't exist. The
            // claim itself succeeded (a Cloudflare row got flipped) so we
            // can't usefully roll back, but the caller is almost certainly
            // about to fail validation upstream anyway.
            _logger.LogWarning(
                "AssignSubdomainToBranch: branch {BranchId} not found when reading PreviewPort. Tunnel {TunnelId} stays at pool placeholder.",
                branchId, claimedRow.TunnelId);
            return;
        }

        if (previewPort == Project.DefaultPreviewPort)
        {
            // Pool placeholder already matches — no PUT needed. Skipping is
            // a meaningful optimisation: the default is the common case for
            // new projects and skipping saves a Cloudflare API round-trip
            // per branch creation.
            return;
        }

        try
        {
            await _cloudflare.AddPublicHostnameAsync(
                claimedRow.TunnelId,
                claimedRow.Hostname,
                previewPort,
                cancellationToken);

            _logger.LogInformation(
                "AssignSubdomainToBranch: reconciled tunnel {TunnelId} ingress to localhost:{PreviewPort} for branch {BranchId}",
                claimedRow.TunnelId, previewPort, branchId);
        }
        catch (Exception ex)
        {
            // Best-effort. RuntimeProvisionerJob's defensive PUT — and
            // UpdateProjectPreviewPort if the user re-saves the port — will
            // catch this up later.
            _logger.LogWarning(
                ex,
                "AssignSubdomainToBranch: Cloudflare ingress PUT failed for tunnel {TunnelId} (branch {BranchId}, port {PreviewPort}). Row remains Assigned; provisioner will retry.",
                claimedRow.TunnelId, branchId, previewPort);
        }
    }
}

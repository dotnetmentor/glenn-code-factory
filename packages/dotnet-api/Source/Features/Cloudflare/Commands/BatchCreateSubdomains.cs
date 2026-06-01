using Microsoft.EntityFrameworkCore;
using Source.Features.Cloudflare.Configuration;
using Source.Features.Cloudflare.Models;
using Source.Features.Cloudflare.Services;
using Source.Features.SystemSettings.Services;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Cloudflare.Commands;

/// <summary>
/// Bulk-provision <paramref name="Count"/> preview-tunnel pool rows. For each
/// iteration:
///
/// <list type="number">
///   <item>Generate a random 8-char prefix via <see cref="SubdomainGenerator"/>.</item>
///   <item>Check uniqueness against the DB; retry on collision (rare).</item>
///   <item>Call Cloudflare to create a tunnel.</item>
///   <item>Configure the tunnel's ingress (public hostname → <c>localhost:5173</c>;
///         the port is the placeholder default — Phase 4 hot-swaps it per-project
///         via the <c>PREVIEW_PORT</c> env var).</item>
///   <item>Create the DNS CNAME.</item>
///   <item>Encrypt the tunnel token + persist a <see cref="SubdomainAssignment"/> row.</item>
/// </list>
///
/// <para><b>Partial-failure semantics.</b> Each iteration runs independently —
/// if one fails (Cloudflare 5xx, transient network error, etc.) we log, count
/// it as a failed slot, and continue to the next iteration. Successful slots
/// are still persisted. The response carries <c>successCount</c> /
/// <c>failedCount</c> so the operator can read the partial result at a
/// glance.</para>
///
/// <para><b>Cancellation.</b> If the caller's <see cref="CancellationToken"/>
/// fires (request aborted), we honour it cleanly between iterations — already-
/// persisted rows stay, in-flight Cloudflare calls have their own timeout.</para>
/// </summary>
public record BatchCreateSubdomainsCommand(int Count)
    : ICommand<Result<BatchCreateSubdomainsResponse>>;

public class BatchCreateSubdomainsHandler
    : ICommandHandler<BatchCreateSubdomainsCommand, Result<BatchCreateSubdomainsResponse>>
{
    /// <summary>
    /// Placeholder ingress port. The runtime overrides per-project via the
    /// <c>PREVIEW_PORT</c> env var in Phase 4; for Phase 1 we just need a valid
    /// port number on the tunnel config so Cloudflare's API accepts the request.
    /// 5173 matches the default project preview port (Vite).
    /// </summary>
    private const int PlaceholderPreviewPort = 5173;

    /// <summary>How many times to retry on a subdomain collision before giving
    /// up the slot. Collisions on a 36⁸ alphabet are astronomically rare — 3
    /// retries is theatre for "we have a guardrail" rather than a real load.</summary>
    private const int MaxCollisionRetries = 3;

    private readonly ApplicationDbContext _db;
    private readonly CloudflareApiClient _cloudflare;
    private readonly ICloudflareOptionsAccessor _cfOptions;
    private readonly ISystemSettingsCipher _cipher;
    private readonly ILogger<BatchCreateSubdomainsHandler> _logger;

    public BatchCreateSubdomainsHandler(
        ApplicationDbContext db,
        CloudflareApiClient cloudflare,
        ICloudflareOptionsAccessor cfOptions,
        ISystemSettingsCipher cipher,
        ILogger<BatchCreateSubdomainsHandler> logger)
    {
        _db = db;
        _cloudflare = cloudflare;
        _cfOptions = cfOptions;
        _cipher = cipher;
        _logger = logger;
    }

    public async Task<Result<BatchCreateSubdomainsResponse>> Handle(
        BatchCreateSubdomainsCommand request,
        CancellationToken cancellationToken)
    {
        // Defence in depth — the controller validates the same range, but a
        // command can be sent through other channels (CLI, background job) so
        // we re-check here.
        if (request.Count < 1 || request.Count > 50)
        {
            return Result.Failure<BatchCreateSubdomainsResponse>(
                "count_out_of_range: count must be between 1 and 50");
        }

        var baseDomain = _cfOptions.Current.BaseDomain;
        if (string.IsNullOrWhiteSpace(baseDomain))
        {
            return Result.Failure<BatchCreateSubdomainsResponse>(
                "base_domain_not_configured: set Cloudflare:BaseDomain in System Settings first");
        }

        var successes = new List<SubdomainAssignmentDto>();
        var failedCount = 0;

        for (var i = 0; i < request.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var dto = await ProvisionOneAsync(baseDomain, cancellationToken);
                successes.Add(dto);
            }
            catch (OperationCanceledException)
            {
                // Caller bailed mid-batch. Stop work and surface what we
                // already persisted — the for-loop's CT check will fire next
                // iteration but we exit the loop explicitly here so the
                // response still includes what made it through.
                _logger.LogInformation(
                    "BatchCreateSubdomains cancelled after {Done}/{Total} slots",
                    successes.Count, request.Count);
                break;
            }
            catch (Exception ex)
            {
                failedCount++;
                _logger.LogWarning(
                    ex,
                    "BatchCreateSubdomains slot {Slot}/{Total} failed: {Message}",
                    i + 1, request.Count, ex.Message);
                // Continue — the spec is "log + still persist successful rows".
            }
        }

        return Result.Success(new BatchCreateSubdomainsResponse
        {
            SuccessCount = successes.Count,
            FailedCount = failedCount,
            Items = successes,
        });
    }

    private async Task<SubdomainAssignmentDto> ProvisionOneAsync(
        string baseDomain,
        CancellationToken ct)
    {
        // 1) Pick a non-colliding prefix.
        var (subdomain, hostname) = await PickUniqueHostnameAsync(baseDomain, ct);

        // 2) Create the tunnel. The name carries the hostname so it's
        //    self-identifying in the Cloudflare dashboard.
        var tunnel = await _cloudflare.CreateTunnelAsync(hostname, ct);

        // 3) Configure ingress + DNS. If either fails, attempt to delete the
        //    tunnel we just created so we don't leave orphaned billable
        //    resources on the operator's Cloudflare account.
        try
        {
            await _cloudflare.AddPublicHostnameAsync(tunnel.Id, hostname, PlaceholderPreviewPort, ct);
            await _cloudflare.EnsureDnsRecordAsync(hostname, tunnel.Id, ct);
        }
        catch
        {
            // Best-effort cleanup. We swallow the cleanup error and re-throw
            // the original — the caller of ProvisionOne sees the original
            // failure cause.
            try
            {
                await _cloudflare.DeleteTunnelAsync(tunnel.Id, CancellationToken.None);
            }
            catch (Exception cleanupEx)
            {
                _logger.LogWarning(
                    cleanupEx,
                    "Best-effort tunnel cleanup failed for orphaned tunnel {TunnelId}",
                    tunnel.Id);
            }
            throw;
        }

        // 4) Encrypt the tunnel token + persist.
        var row = new SubdomainAssignment
        {
            Hostname = hostname,
            Subdomain = subdomain,
            TunnelId = tunnel.Id,
            TunnelToken = _cipher.Encrypt(tunnel.Token),
            Status = SubdomainStatus.Available,
        };
        _db.SubdomainAssignments.Add(row);
        await _db.SaveChangesAsync(ct);

        return new SubdomainAssignmentDto
        {
            Id = row.Id,
            Hostname = row.Hostname,
            Subdomain = row.Subdomain,
            Status = row.Status,
            CreatedAt = row.CreatedAt,
            AssignedBranchId = row.AssignedBranchId,
            AssignedAt = row.AssignedAt,
        };
    }

    private async Task<(string Subdomain, string Hostname)> PickUniqueHostnameAsync(
        string baseDomain,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxCollisionRetries; attempt++)
        {
            var subdomain = SubdomainGenerator.Generate();
            var hostname = $"{subdomain}.{baseDomain}";

            // IgnoreQueryFilters so a soft-deleted row with the same hostname
            // still blocks reuse — the unique index would 23505 us otherwise.
            var exists = await _db.SubdomainAssignments
                .IgnoreQueryFilters()
                .AnyAsync(s => s.Hostname == hostname, ct);

            if (!exists)
            {
                return (subdomain, hostname);
            }

            _logger.LogInformation(
                "Subdomain collision on attempt {Attempt}; retrying", attempt + 1);
        }

        throw new InvalidOperationException(
            $"Failed to generate a unique subdomain after {MaxCollisionRetries} attempts. " +
            "Pool may be saturated — extremely unlikely on a 36^8 alphabet.");
    }
}

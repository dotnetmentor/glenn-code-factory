using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Source.Shared;

namespace Source.Infrastructure.ErrorHandling;

/// <summary>
/// Daily retention job that bulk-deletes <see cref="Source.Features.ErrorLog.Models.ErrorLog"/>
/// rows older than <see cref="ErrorCaptureOptions.RetentionDays"/>.
///
/// Invariants:
/// - Never touches <see cref="Source.Features.ErrorLog.Models.ErrorSignature"/> rows
///   (they are small, high-signal, and the dashboard's primary source of truth).
///   This is enforced structurally: the <c>Where</c> clause below only targets
///   <see cref="Source.Infrastructure.ApplicationDbContext.ErrorLogs"/>.
/// - Uses <see cref="EntityFrameworkQueryableExtensions.ExecuteDeleteAsync"/> so nothing
///   is loaded into memory — critical for tables that may grow to millions of rows.
/// - Uses <see cref="IClock"/> rather than <c>DateTime.UtcNow</c> so tests can pin time.
/// - Database exceptions are logged and re-thrown so Hangfire's retry pipeline engages.
/// </summary>
public class ErrorLogRetentionJob
{
    private readonly ApplicationDbContext _db;
    private readonly IOptions<ErrorCaptureOptions> _options;
    private readonly IClock _clock;
    private readonly ILogger<ErrorLogRetentionJob> _logger;

    public ErrorLogRetentionJob(
        ApplicationDbContext db,
        IOptions<ErrorCaptureOptions> options,
        IClock clock,
        ILogger<ErrorLogRetentionJob> logger)
    {
        _db = db;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken ct)
    {
        var retentionDays = _options.Value.RetentionDays;
        var cutoff = _clock.UtcNow - TimeSpan.FromDays(retentionDays);

        try
        {
            int deletedCount;

            // The EF InMemory provider does not implement ExecuteDeleteAsync
            // (throws "not supported by the current database provider"). Since we
            // still want to exercise this job end-to-end in tests, fall back to a
            // RemoveRange path when running against InMemory. Production (Postgres)
            // always takes the bulk path.
            if (_db.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
            {
                var stale = await _db.ErrorLogs
                    .Where(e => e.CreatedAt < cutoff)
                    .ToListAsync(ct);
                _db.ErrorLogs.RemoveRange(stale);
                await _db.SaveChangesAsync(ct);
                deletedCount = stale.Count;
            }
            else
            {
                deletedCount = await _db.ErrorLogs
                    .Where(e => e.CreatedAt < cutoff)
                    .ExecuteDeleteAsync(ct);
            }

            _logger.LogInformation(
                "ErrorLogRetentionJob deleted {Count} rows older than {Cutoff}",
                deletedCount,
                cutoff);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "ErrorLogRetentionJob failed while deleting rows older than {Cutoff}",
                cutoff);
            throw;
        }
    }
}

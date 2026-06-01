namespace Source.Features.FlyManagement.Models;

/// <summary>
/// Lifecycle state of a single Fly API call recorded in <see cref="FlyOperation"/>.
/// Stored as a string in the database (see ApplicationDbContext configuration) so
/// adding new states later doesn't break existing rows.
/// </summary>
public enum FlyOperationStatus
{
    /// <summary>Row written before the HTTP call completed — used for in-flight tracing.</summary>
    Pending,

    /// <summary>The Fly API returned a 2xx response.</summary>
    Succeeded,

    /// <summary>The Fly API returned a non-2xx response, timed out, or threw.</summary>
    Failed,
}

namespace Source.Features.BoxManagement.Models;

/// <summary>
/// Lifecycle of a single audited Box API call. Persisted as a string so adding
/// states later never breaks existing rows.
/// </summary>
public enum BoxOperationStatus
{
    /// <summary>Row written, HTTP call not yet completed.</summary>
    Pending,

    /// <summary>Box returned 2xx; <see cref="BoxOperation.ResponsePayload"/> holds the body.</summary>
    Succeeded,

    /// <summary>Non-2xx or transport failure; <see cref="BoxOperation.ErrorCode"/> explains.</summary>
    Failed,
}

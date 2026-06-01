namespace Source.Infrastructure.ErrorHandling;

/// <summary>
/// Represents an error entry to be queued for persistence.
/// </summary>
public record ErrorEntry(
    string Message,
    string? StackTrace,
    string Source,
    string Severity,
    string? CorrelationId,
    string? RequestPath,
    string? RequestMethod,
    string? ContextData,
    DateTime OccurredAt
);

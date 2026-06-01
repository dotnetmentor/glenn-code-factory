namespace Source.Infrastructure.ErrorHandling;

/// <summary>
/// Options for the error capture pipeline. Bound from the "ErrorCapture" section
/// of <c>appsettings.json</c>.
/// </summary>
public class ErrorCaptureOptions
{
    public const string SectionName = "ErrorCapture";

    /// <summary>
    /// How many days of <see cref="Source.Features.ErrorLog.Models.ErrorLog"/> samples
    /// to keep before <see cref="ErrorLogRetentionJob"/> deletes them. Default is 90.
    /// </summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>
    /// How often <see cref="ErrorPipelineSummaryReporter"/> emits its single-line
    /// summary of the pipeline counters + queue depth. Default is 60 seconds; tests
    /// may set this to a small fraction of a second to exercise the timer loop
    /// quickly. Accepts a <c>double</c> so sub-second intervals are expressible.
    /// </summary>
    public double SummaryIntervalSeconds { get; set; } = 60;
}

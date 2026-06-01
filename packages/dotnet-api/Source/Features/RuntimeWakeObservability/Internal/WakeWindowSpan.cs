using Source.Shared.Results;

namespace Source.Features.RuntimeWakeObservability.Internal;

/// <summary>
/// Parses the <c>window</c> query parameter — one of <c>1h</c> / <c>24h</c> /
/// <c>7d</c> — into a concrete UTC <c>(start, end]</c> span anchored at "now".
///
/// <para>Centralised so the three handlers don't drift on what counts as a valid
/// window. Anything outside the allow-list returns <see cref="Result.Failure"/>
/// with a stable error string so the controller can shape it into a 400.</para>
/// </summary>
public static class WakeWindowSpan
{
    /// <summary>The set of accepted <c>window</c> tokens, normalised to lowercase.</summary>
    public static readonly IReadOnlySet<string> AcceptedWindows = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "1h",
        "24h",
        "7d",
    };

    /// <summary>
    /// Resolve <paramref name="window"/> into a UTC <c>(start, end]</c> span
    /// ending at <paramref name="nowUtc"/>. Returns failure when the token is
    /// missing or not in <see cref="AcceptedWindows"/>.
    /// </summary>
    public static Result<(DateTime Start, DateTime End)> TryParse(string? window, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(window))
        {
            return Result.Failure<(DateTime, DateTime)>("window is required (one of: 1h, 24h, 7d)");
        }

        var normalised = window.Trim().ToLowerInvariant();
        var span = normalised switch
        {
            "1h" => TimeSpan.FromHours(1),
            "24h" => TimeSpan.FromHours(24),
            "7d" => TimeSpan.FromDays(7),
            _ => (TimeSpan?)null,
        };

        if (span is null)
        {
            return Result.Failure<(DateTime, DateTime)>("window must be one of: 1h, 24h, 7d");
        }

        return Result.Success((nowUtc - span.Value, nowUtc));
    }
}

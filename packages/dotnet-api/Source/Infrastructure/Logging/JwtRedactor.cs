using System.Text.RegularExpressions;

namespace Source.Infrastructure.Logging;

/// <summary>
/// Last-line-of-defence scrubber for JWT-shaped substrings in arbitrary text.
///
/// Even with disciplined logging, a JWT can sneak in (verbose Fly request bodies,
/// exception serialisations, payload strings that happen to embed a token). This
/// redactor is invoked from <see cref="RedactingLoggerProvider"/> to rewrite any
/// formatted log message before it reaches a sink.
///
/// Pattern: three dot-separated base64url segments where the first segment starts
/// with <c>eyJ</c> — that's a JWT header that decodes to <c>{"...</c>.
///
/// Kept as a pure static class so it's directly unit-testable without spinning up
/// a logger, and so callers don't pay per-call regex construction cost.
/// </summary>
public static class JwtRedactor
{
    private static readonly Regex JwtPattern = new(
        @"eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public const string Replacement = "eyJ***REDACTED***";

    /// <summary>
    /// Returns true if <paramref name="input"/> contains at least one JWT-shaped
    /// substring. Used by callers as a cheap fast-path before allocating the
    /// replaced string — most log lines never contain a token.
    /// </summary>
    public static bool ContainsMatch(string? input) =>
        !string.IsNullOrEmpty(input) && JwtPattern.IsMatch(input);

    /// <summary>
    /// Returns <paramref name="input"/> with every JWT-shaped substring replaced
    /// by <see cref="Replacement"/>. Null/empty input returns empty string —
    /// callers can rely on the result being non-null.
    /// </summary>
    public static string Redact(string? input) =>
        string.IsNullOrEmpty(input) ? input ?? string.Empty : JwtPattern.Replace(input, Replacement);
}

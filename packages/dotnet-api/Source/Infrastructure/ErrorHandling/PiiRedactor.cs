using System.Text.RegularExpressions;

namespace Source.Infrastructure.ErrorHandling;

/// <summary>
/// Redacts common PII / secret patterns from arbitrary text before it touches the
/// error pipeline. Applied on the single hot path (<see cref="ErrorQueue.EnqueueAsync"/>)
/// so that stored <c>ErrorLog</c> rows are safe to share with the whole team.
///
/// Deny-listed by pattern (not allow-listed by field): we can't trust field names to
/// contain or not contain secrets. See <c>resilient-error-capture-pipeline</c> spec.
/// </summary>
public interface IPiiRedactor
{
    /// <summary>
    /// Returns a copy of <paramref name="input"/> with all matching PII patterns
    /// replaced by placeholders. Null and empty inputs are returned unchanged.
    /// Must never throw.
    /// </summary>
    string? Redact(string? input);
}

public sealed partial class PiiRedactor : IPiiRedactor
{
    // Pattern order matters. Connection strings are matched FIRST so the Password=...
    // kv-secret pattern does not fire on the connection string's interior.
    //
    // Postgres connection strings:
    // start with Host= or Server=, followed by kvps (separated by ;) that include
    // Password= as one of the pairs. Matched greedily up to the last kvp that belongs
    // to the connstring. The boundary is: stop at the first run of whitespace that is
    // NOT inside another kvp.
    [GeneratedRegex(
        @"\b(?:Host|Server)=[^;\s]+(?:;[A-Za-z0-9_]+=[^;\s]+)*;Password=[^;\s]+(?:;[A-Za-z0-9_]+=[^;\s]+)*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionStringRegex();

    // Email
    [GeneratedRegex(
        @"[a-zA-Z0-9+_.-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}",
        RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    // Bearer token — the whole "Bearer <token>" phrase
    [GeneratedRegex(
        @"Bearer\s+[A-Za-z0-9\-._~+/]+=*",
        RegexOptions.CultureInvariant)]
    private static partial Regex BearerRegex();

    // JWT — anchor on eyJ (ASCII "{"-prefixed base64 JSON header) to reduce false positives
    [GeneratedRegex(
        @"eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}",
        RegexOptions.CultureInvariant)]
    private static partial Regex JwtRegex();

    // Key-value secrets. Preserves the key name; captures the value.
    [GeneratedRegex(
        @"(?i)\b(password|apikey|api_key|secret|token)\s*=\s*[^\s;,&]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueSecretRegex();

    // Credit-card-shaped 16 digits. Spaced variants are a documented trade-off.
    [GeneratedRegex(
        @"\b\d{16}\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex Card16Regex();

    public string? Redact(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        try
        {
            var s = input;

            // Order matters — connstring before kv-secret, otherwise Password=... inside
            // a connection string would be rewritten piecemeal and escape the whole-match.
            s = ConnectionStringRegex().Replace(s, "<connstring>");
            s = EmailRegex().Replace(s, "<email>");
            s = BearerRegex().Replace(s, "Bearer <redacted>");
            s = JwtRegex().Replace(s, "<jwt>");
            s = KeyValueSecretRegex().Replace(s, m => $"{m.Groups[1].Value}=<redacted>");
            s = Card16Regex().Replace(s, "<card>");

            return s;
        }
        catch
        {
            // Redaction is safety-net code on a hot path; degrade to raw input rather
            // than throw into the error pipeline.
            return input;
        }
    }
}

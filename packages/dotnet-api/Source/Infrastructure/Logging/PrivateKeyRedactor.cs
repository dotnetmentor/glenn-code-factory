using System.Text.RegularExpressions;

namespace Source.Infrastructure.Logging;

/// <summary>
/// Last-line-of-defence scrubber for SSH-private-key-shaped substrings in
/// arbitrary text. Sibling to <see cref="JwtRedactor"/>; same shape (pure
/// static, regex compiled once) so the wire-up is symmetric.
///
/// Pattern matches the standard PEM header → body → footer envelope:
/// <c>-----BEGIN ... PRIVATE KEY-----</c> through
/// <c>-----END ... PRIVATE KEY-----</c>. The body is allowed to span newlines
/// (compiled with <see cref="RegexOptions.Singleline"/> so <c>.</c> matches
/// <c>\n</c>) because every real OpenSSH/PEM dump is multi-line.
///
/// Even with disciplined logging, a key can sneak in (verbose request bodies,
/// exception serialisations from <c>git push</c> stderr, payload strings that
/// happen to embed the key). Wired into the same redacting logger pipeline as
/// the JWT scrubber.
/// </summary>
public static class PrivateKeyRedactor
{
    // Greedy match would over-eat across multiple keys in one log line; lazy
    // (.*?) keeps each key independently scrubbed.
    private static readonly Regex KeyPattern = new(
        @"-----BEGIN [^-]+ PRIVATE KEY-----.*?-----END [^-]+ PRIVATE KEY-----",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    public const string Replacement = "[REDACTED PRIVATE KEY]";

    /// <summary>
    /// Returns true if <paramref name="input"/> contains at least one
    /// PEM-private-key-shaped substring. Used by callers as a cheap fast-path
    /// before allocating the replaced string — most log lines never carry one.
    /// </summary>
    public static bool ContainsMatch(string? input) =>
        !string.IsNullOrEmpty(input) && KeyPattern.IsMatch(input);

    /// <summary>
    /// Returns <paramref name="input"/> with every private-key envelope
    /// replaced by <see cref="Replacement"/>. Null/empty input returns empty
    /// string — callers can rely on the result being non-null.
    /// </summary>
    public static string Redact(string? input) =>
        string.IsNullOrEmpty(input) ? input ?? string.Empty : KeyPattern.Replace(input, Replacement);
}

using System.Text.RegularExpressions;

namespace Source.Infrastructure.Logging;

/// <summary>
/// Last-line-of-defence scrubber for secret-value-shaped substrings in arbitrary
/// text. Sibling to <see cref="JwtRedactor"/> and <see cref="PrivateKeyRedactor"/>;
/// same shape (pure static, regex compiled once) so the wire-up is symmetric.
///
/// === Parity with the daemon ===
///
/// The daemon has a mirror-image scrubber at
/// <c>packages/daemon/src/logging/secretRedactor.ts</c>. The two halves carry
/// the SAME regex set (same names, same patterns, same default-enabled flags)
/// so coverage stays in lockstep. When you change a regex here, change the
/// daemon side in the same commit. The reason is defence-in-depth: a leaked
/// project secret can flow through either runtime's logs, and we want one
/// reviewer to be able to compare both files diff-by-diff.
///
/// === Why these patterns ===
///
/// Project-secrets (Spec 14) lets users store API keys, OAuth tokens, etc. as
/// per-project env vars that the daemon injects into hook subprocesses. Even
/// with disciplined logging upstream (we redact known property names already),
/// a secret can sneak in via verbose subprocess stderr, exception messages,
/// or third-party libs that log request bodies. The regexes below match the
/// well-known formats issued by major providers; <see cref="GenericHighEntropy"/>
/// would catch the long tail but is disabled by default — see comment there.
///
/// === Replacement ===
///
/// Every match is replaced with the literal <see cref="Replacement"/> string.
/// We don't preserve a prefix the way <see cref="JwtRedactor"/> does
/// ("eyJ***REDACTED***") because secret values don't have a stable visual
/// signature operators look for in logs — a flat "[REDACTED]" is clearer.
/// </summary>
public static class SecretValueRedactor
{
    public const string Replacement = "[REDACTED]";

    // ------------------------------------------------------------------------
    // Regex set — keep in sync with packages/daemon/src/logging/secretRedactor.ts
    // ------------------------------------------------------------------------
    //
    // Each pattern is named for its issuer/format. All compiled once with
    // RegexOptions.Compiled for the per-call hot path. Word-boundary anchors
    // (\b) bracket each pattern so we don't smear into surrounding text in
    // structured log lines like `{"key":"sk_live_..."}`.

    /// <summary>Stripe secret keys: <c>sk_test_…</c> / <c>sk_live_…</c> / <c>sk_…</c>.</summary>
    public static readonly Regex StripeSecretLike = new(
        @"\bsk_(test_|live_)?[A-Za-z0-9]{20,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Stripe publishable keys: <c>pk_test_…</c> / <c>pk_live_…</c> / <c>pk_…</c>.
    /// Lower risk than secret keys (publishable by definition) but still PII-grade
    /// in some flows; cheap to redact.</summary>
    public static readonly Regex StripePublicLike = new(
        @"\bpk_(test_|live_)?[A-Za-z0-9]{20,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>HTTP Bearer-token headers. Case-insensitive on the literal
    /// "Bearer" prefix because some clients emit lowercase. The whole "Bearer X"
    /// run is replaced — mirroring how <see cref="JwtRedactor"/> swallows the
    /// whole token (it doesn't keep the surrounding word). The token itself
    /// might separately match <see cref="JwtRedactor"/> (which runs first in
    /// <see cref="RedactingLoggerProvider"/>); if so the JWT pattern wins and
    /// this regex finds nothing. That's fine — both end up redacted.</summary>
    public static readonly Regex BearerToken = new(
        @"\bBearer\s+[A-Za-z0-9._-]{20,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>AWS access key IDs: <c>AKIA</c> + 16 uppercase alphanumerics.
    /// AWS publishes this exact shape; matches deterministically.</summary>
    public static readonly Regex AwsAccessKey = new(
        @"\bAKIA[0-9A-Z]{16}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>OpenAI keys: <c>sk-…</c> or <c>sk-proj-…</c>. Note the dash —
    /// distinct from Stripe's underscore-separated <c>sk_…</c>, so the two
    /// patterns don't collide.</summary>
    public static readonly Regex OpenAIKey = new(
        @"\bsk-(proj-)?[A-Za-z0-9_-]{20,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Generic high-entropy strings of length >= 40. This catches long
    /// base64/hex secrets that don't match any provider-specific shape
    /// (Postgres URLs with embedded passwords, generic API keys, etc.).
    ///
    /// DISABLED BY DEFAULT: real-world false-positive rate is too high — UUIDs
    /// concatenated with slashes (Git refs, S3 paths), file content hashes,
    /// log correlation IDs, and base64-encoded telemetry blobs all hit this.
    /// Enabling it scrubs huge swaths of legitimate logs and makes
    /// triage harder than the leak it's meant to catch.
    ///
    /// Kept defined so a constructor flag (<see cref="Redact(string,bool)"/>)
    /// can opt in for environments where the calculus differs (e.g. customer
    /// reports a leak via this exact shape and we need the backstop NOW).
    /// </summary>
    public static readonly Regex GenericHighEntropy = new(
        @"\b[A-Za-z0-9+/=_-]{40,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // The default set — every regex except GenericHighEntropy. Listed in the
    // order they run; ordering doesn't affect correctness because each match
    // is replaced wholesale, but stable order keeps test output deterministic.
    private static readonly Regex[] DefaultPatterns =
    {
        StripeSecretLike,
        StripePublicLike,
        BearerToken,
        AwsAccessKey,
        OpenAIKey,
    };

    private static readonly Regex[] AllPatternsIncludingHighEntropy =
    {
        StripeSecretLike,
        StripePublicLike,
        BearerToken,
        AwsAccessKey,
        OpenAIKey,
        GenericHighEntropy,
    };

    /// <summary>
    /// Returns true if <paramref name="input"/> contains at least one
    /// secret-shaped substring under the default pattern set. Used by callers
    /// (e.g. <see cref="RedactingLoggerProvider"/>) as a cheap fast-path
    /// before allocating the replaced string.
    /// </summary>
    public static bool ContainsMatch(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return false;
        }
        foreach (var pattern in DefaultPatterns)
        {
            if (pattern.IsMatch(input))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Returns <paramref name="input"/> with every matching secret-shaped
    /// substring replaced by <see cref="Replacement"/>. Default set excludes
    /// <see cref="GenericHighEntropy"/>; pass <paramref name="includeHighEntropy"/>=true
    /// to opt in. Null/empty input returns empty string.
    /// </summary>
    public static string Redact(string? input, bool includeHighEntropy = false)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input ?? string.Empty;
        }
        var patterns = includeHighEntropy ? AllPatternsIncludingHighEntropy : DefaultPatterns;
        var current = input;
        foreach (var pattern in patterns)
        {
            current = pattern.Replace(current, Replacement);
        }
        return current;
    }
}

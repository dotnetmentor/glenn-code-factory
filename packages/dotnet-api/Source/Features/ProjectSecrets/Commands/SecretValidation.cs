using System.Text.RegularExpressions;

namespace Source.Features.ProjectSecrets.Commands;

/// <summary>
/// Shared validation primitives for the secret CRUD commands. Pulled out of the
/// individual command handlers so the rules sit in one place and the
/// failure-code strings are typo-proof for the controller / Orval mapping.
///
/// <list type="bullet">
///   <item><b>Key format:</b> <c>^[A-Za-z][A-Za-z0-9_]*$</c> — letters, digits,
///         and underscores (supports .NET-style keys like <c>Jwt__Key</c>).
///         Length 1..200 to mirror the column limit on
///         <see cref="Models.ProjectSecret.Key"/>.</item>
///   <item><b>Plaintext:</b> rejects any value containing <c>'\n'</c>. The
///         daemon's bootstrap path emits a plain <c>KEY=VALUE</c> env file with
///         no quoting; a newline would silently cut the value in half. Fail
///         loudly here rather than corrupt the runtime.</item>
/// </list>
/// </summary>
internal static partial class SecretValidation
{
    public const int MaxKeyLength = 200;

    public const string ErrorInvalidKeyFormat = "invalid_key_format";
    public const string ErrorInvalidPlaintext = "invalid_plaintext";

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyRegex();

    /// <summary>
    /// Returns null on success, or the failure code (e.g.
    /// <see cref="ErrorInvalidKeyFormat"/>) when the key is unacceptable.
    /// </summary>
    public static string? ValidateKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return ErrorInvalidKeyFormat;
        if (key.Length > MaxKeyLength) return ErrorInvalidKeyFormat;
        return KeyRegex().IsMatch(key) ? null : ErrorInvalidKeyFormat;
    }

    /// <summary>
    /// Returns null on success, or the failure code when the plaintext would
    /// break the daemon's KEY=VALUE env-file format. A null plaintext is
    /// treated as "not set" by callers; this method validates the non-null
    /// case only.
    /// </summary>
    public static string? ValidatePlaintext(string? plaintext)
    {
        if (plaintext is null) return ErrorInvalidPlaintext;
        if (plaintext.Contains('\n')) return ErrorInvalidPlaintext;
        return null;
    }
}

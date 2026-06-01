using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Source.Infrastructure.ErrorHandling;

/// <summary>
/// Computes a stable, deterministic 64-char lowercase hex signature for an
/// <see cref="ErrorEntry"/>. Signatures group "the same failure" across
/// time without fragmenting on unrelated code changes.
///
/// Algorithm:
/// 1. Parse the exception type name from the first line of the stack trace
///    (content before the first ':').
/// 2. Extract the top 3 stack frames, each normalized to "Namespace.Type.Method:line".
/// 3. SHA256 over UTF-8 bytes of: Source + "\n" + ExceptionType + "\n" + top-3 frames.
/// 4. If the stack trace is null, empty, or no frames could be parsed, hash
///    Source + "\n" + ExceptionType + "\n" + first 200 chars of Message instead.
/// 5. Return hex-encoded, lowercased (exactly 64 chars).
///
/// Design decisions:
/// - <b>Source is part of the signature</b>. Two errors with identical stacks but
///   different Source (e.g. Frontend vs HTTP) should remain distinct — they travel
///   through different triage paths, have different owners, and are frequently
///   root-caused differently. Collapsing them hurts more than it helps.
/// - <b>Frames beyond the top 3 are ignored</b>. This is the primary guard against
///   fingerprint fragmentation when unrelated code shifts line numbers in deeper
///   frames across releases (see spec risk: "Fingerprint fragmentation").
/// - <b>Never throws</b>. On any parsing failure we fall back to hashing the raw
///   Message + StackTrace text; on catastrophic failure we hash a constant.
/// </summary>
public interface IErrorSignatureHasher
{
    string Hash(ErrorEntry entry);
}

public sealed class ErrorSignatureHasher : IErrorSignatureHasher
{
    // Matches: "   at Namespace.Type.Method(args) in File.cs:line 42"
    // or:      "   at Namespace.Type.Method(args)" (no file/line info)
    // Captures: group 1 = "Namespace.Type.Method", group 2 = line number (optional).
    private static readonly Regex FrameRegex = new(
        @"^\s*at\s+(?<target>[^\s(]+)\s*\([^)]*\)(?:\s+in\s+.+?:line\s+(?<line>\d+))?",
        RegexOptions.Compiled);

    public string Hash(ErrorEntry entry)
    {
        try
        {
            var source = entry.Source ?? string.Empty;
            var (exceptionType, frames) = ParseStackTrace(entry.StackTrace);

            string payload;
            if (frames.Count == 0)
            {
                // Fallback: type + message prefix. Still keyed by source.
                var message = entry.Message ?? string.Empty;
                var prefix = message.Length <= 200 ? message : message.Substring(0, 200);
                payload = string.Concat(source, "\n", exceptionType, "\n", prefix);
            }
            else
            {
                var top = frames.Count > 3 ? frames.GetRange(0, 3) : frames;
                payload = string.Concat(source, "\n", exceptionType, "\n", string.Join("\n", top));
            }

            return Sha256Hex(payload);
        }
        catch
        {
            // Catastrophic fallback — must never throw into pipeline code.
            try
            {
                var raw = string.Concat(
                    entry?.Source ?? string.Empty,
                    "\n",
                    entry?.Message ?? string.Empty,
                    "\n",
                    entry?.StackTrace ?? string.Empty);
                return Sha256Hex(raw);
            }
            catch
            {
                return Sha256Hex("error-signature-hasher-fallback");
            }
        }
    }

    private static (string exceptionType, List<string> frames) ParseStackTrace(string? stackTrace)
    {
        if (string.IsNullOrWhiteSpace(stackTrace))
        {
            return (string.Empty, new List<string>());
        }

        var lines = stackTrace.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return (string.Empty, new List<string>());
        }

        // First line, trimmed, up to the first ':'. If the first line looks like
        // a frame ("   at ..."), we have no exception type header; use empty.
        var firstLine = lines[0].TrimStart();
        string exceptionType;
        int firstFrameLineIndex;
        if (firstLine.StartsWith("at ", StringComparison.Ordinal))
        {
            exceptionType = string.Empty;
            firstFrameLineIndex = 0;
        }
        else
        {
            var colonIdx = firstLine.IndexOf(':');
            exceptionType = colonIdx >= 0 ? firstLine.Substring(0, colonIdx).Trim() : firstLine.Trim();
            firstFrameLineIndex = 1;
        }

        var frames = new List<string>(capacity: 3);
        for (var i = firstFrameLineIndex; i < lines.Length && frames.Count < 16; i++)
        {
            var match = FrameRegex.Match(lines[i]);
            if (!match.Success) continue;

            var target = match.Groups["target"].Value;
            var line = match.Groups["line"].Success ? match.Groups["line"].Value : "0";
            frames.Add(string.Concat(target, ":", line));
        }

        return (exceptionType, frames);
    }

    private static string Sha256Hex(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

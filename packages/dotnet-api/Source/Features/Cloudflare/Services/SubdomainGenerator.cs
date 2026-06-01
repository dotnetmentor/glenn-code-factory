using System.Security.Cryptography;

namespace Source.Features.Cloudflare.Services;

/// <summary>
/// Cryptographically-random subdomain prefix generator. Produces 8 characters
/// from the alphabet <c>abcdefghijklmnopqrstuvwxyz0123456789</c>.
///
/// <para>The alphabet space is 36⁸ ≈ 2.82 × 10¹² combinations — collisions are
/// vanishingly rare in practice but possible, so the batch-create handler does
/// a DB uniqueness check + retry. We deliberately use
/// <see cref="RandomNumberGenerator"/> rather than <see cref="Random"/> so the
/// generated subdomain can't be guessed from a wall-clock seed (a guessable
/// preview URL is a footgun: the runtime is reachable on the public internet
/// before the user has even shared it).</para>
///
/// <para><b>What this returns.</b> The 8-char prefix only — no dot, no base
/// domain. The caller composes the full hostname by appending
/// <c>.{baseDomain}</c> from <c>SystemSettings.Cloudflare:BaseDomain</c>.</para>
/// </summary>
public static class SubdomainGenerator
{
    /// <summary>The alphabet a subdomain prefix may draw from. Lowercase
    /// letters + digits — every char is DNS-label-safe and humans can read
    /// the result out loud over a phone call without ambiguity.</summary>
    public const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>Length of every generated prefix. Fixed at 8 — the spec is explicit.</summary>
    public const int Length = 8;

    /// <summary>
    /// Generate a single random 8-char prefix. Thread-safe — internally calls
    /// <see cref="RandomNumberGenerator.GetBytes(int)"/> which draws from a
    /// per-thread CSPRNG.
    /// </summary>
    public static string Generate()
    {
        // Rejection-sample to keep the distribution unbiased: 256 / 36 = 7.11,
        // so the largest multiple of 36 that fits in a byte is 252. We treat
        // any byte ≥ 252 as a reroll. Worst-case rejection rate is ~1.6%,
        // which is fine — the generator is not on a hot path.
        const int maxAcceptable = 256 - (256 % 36); // = 252

        Span<char> result = stackalloc char[Length];
        var produced = 0;

        // Allocate generously so the common case completes in a single
        // RandomNumberGenerator.Fill call; if we exhaust the buffer (vanishingly
        // unlikely at 1.6% rejection), we top up and continue.
        Span<byte> buffer = stackalloc byte[Length * 2];
        var bufferOffset = buffer.Length; // forces an initial fill

        while (produced < Length)
        {
            if (bufferOffset >= buffer.Length)
            {
                RandomNumberGenerator.Fill(buffer);
                bufferOffset = 0;
            }

            var b = buffer[bufferOffset++];
            if (b >= maxAcceptable)
            {
                continue;
            }

            result[produced++] = Alphabet[b % 36];
        }

        return new string(result);
    }
}

using System.Text;
using System.Text.Json;
using Source.Features.ProjectSecrets.Services;

namespace Source.Features.Projects.Services;

/// <summary>
/// Tiny envelope helper that lets the BYOK flow store everything
/// <see cref="SecretEncryptionService"/> needs for decryption (ciphertext,
/// nonce, DEK version) inside a single nullable text column on the
/// <c>Project</c> row.
///
/// <para><b>Wire format.</b> JSON <c>{"v":1,"c":"&lt;b64&gt;","n":"&lt;b64&gt;","d":&lt;int&gt;}</c>
/// then base64-encoded as a whole. The outer base64 keeps the value safe to
/// drop into any text-shaped column / log-redaction pipeline that already
/// knows how to elide base64 blobs. <c>v</c> is a schema version we can bump
/// if the inner shape ever changes (e.g. AAD fields added) without breaking
/// rows that were written under v1.</para>
///
/// <para><b>What this class is NOT.</b> It is not encryption. The actual
/// AES-256-GCM round-trip is owned by <see cref="SecretEncryptionService"/>
/// — this helper just packs / unpacks the three primitive pieces it returns.
/// We never log or echo the wrapped envelope itself, even though the
/// ciphertext component is already AEAD-protected.</para>
/// </summary>
public static class ProjectByokEnvelope
{
    private const int CurrentVersion = 1;

    /// <summary>
    /// Pack a freshly-encrypted secret into the storage envelope format.
    /// </summary>
    public static string Pack(byte[] ciphertext, byte[] nonce, int dekVersion)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(nonce);

        var inner = JsonSerializer.SerializeToUtf8Bytes(new EnvelopeJson(
            v: CurrentVersion,
            c: Convert.ToBase64String(ciphertext),
            n: Convert.ToBase64String(nonce),
            d: dekVersion));
        return Convert.ToBase64String(inner);
    }

    /// <summary>
    /// Unpack a stored envelope back into the three raw fields. Throws
    /// <see cref="InvalidDataException"/> on a malformed / wrong-version row
    /// so the caller can decide whether to fail closed or skip the field.
    /// </summary>
    public static (byte[] Ciphertext, byte[] Nonce, int DekVersion) Unpack(string envelope)
    {
        if (string.IsNullOrWhiteSpace(envelope))
        {
            throw new InvalidDataException("BYOK envelope is empty.");
        }

        byte[] inner;
        try
        {
            inner = Convert.FromBase64String(envelope);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("BYOK envelope is not valid base64.", ex);
        }

        EnvelopeJson? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<EnvelopeJson>(inner);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("BYOK envelope JSON is malformed.", ex);
        }

        if (parsed is null)
        {
            throw new InvalidDataException("BYOK envelope JSON deserialised to null.");
        }
        if (parsed.v != CurrentVersion)
        {
            throw new InvalidDataException(
                $"BYOK envelope is version {parsed.v}; this build only handles v{CurrentVersion}.");
        }
        if (string.IsNullOrEmpty(parsed.c) || string.IsNullOrEmpty(parsed.n))
        {
            throw new InvalidDataException("BYOK envelope is missing ciphertext or nonce.");
        }

        byte[] ciphertext;
        byte[] nonce;
        try
        {
            ciphertext = Convert.FromBase64String(parsed.c);
            nonce = Convert.FromBase64String(parsed.n);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("BYOK envelope ciphertext / nonce are not valid base64.", ex);
        }

        return (ciphertext, nonce, parsed.d);
    }

    // Lower-case property names to keep the on-disk JSON compact and stable.
    private sealed record EnvelopeJson(int v, string c, string n, int d);
}

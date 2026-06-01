using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Source.Features.SystemSettings.Services;

/// <summary>
/// Stateless AES-256-GCM round-trip for SystemSetting secret values.
/// Storage format is <c>base64(nonce(12) || ciphertext || tag(16))</c> — single string,
/// no extra columns, easy to round-trip.
/// </summary>
public interface ISystemSettingsCipher
{
    string Encrypt(string plaintext);
    string Decrypt(string cipherBase64);
}

public class SystemSettingsCipherOptions
{
    public const string SectionName = "SystemSettings";

    /// <summary>Base64-encoded 32-byte master key. Empty string is treated as "missing".</summary>
    public string EncryptionKey { get; set; } = string.Empty;
}

public class SystemSettingsCipher : ISystemSettingsCipher
{
    private const int NonceSize = 12;        // AesGcm.NonceByteSizes max
    private const int TagSize = 16;          // AesGcm.TagByteSizes max
    private const int KeySize = 32;          // AES-256

    private readonly byte[] _key;

    public SystemSettingsCipher(IOptions<SystemSettingsCipherOptions> options)
    {
        var raw = options.Value.EncryptionKey;
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException(
                "SystemSettings:EncryptionKey is not configured. " +
                "Set it to a base64-encoded 32-byte value in appsettings (Production fails fast; " +
                "Development auto-generates one on first boot).");
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(raw);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                "SystemSettings:EncryptionKey is not valid base64.", ex);
        }

        if (decoded.Length != KeySize)
        {
            throw new InvalidOperationException(
                $"SystemSettings:EncryptionKey must decode to exactly {KeySize} bytes (got {decoded.Length}).");
        }

        _key = decoded;
    }

    public string Encrypt(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var plaintextBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        var combined = new byte[NonceSize + ciphertext.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, combined, 0, NonceSize);
        Buffer.BlockCopy(ciphertext, 0, combined, NonceSize, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, combined, NonceSize + ciphertext.Length, TagSize);

        return Convert.ToBase64String(combined);
    }

    public string Decrypt(string cipherBase64)
    {
        ArgumentNullException.ThrowIfNull(cipherBase64);

        byte[] combined;
        try
        {
            combined = Convert.FromBase64String(cipherBase64);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("Stored SystemSetting value is not valid base64.", ex);
        }

        if (combined.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Stored SystemSetting value is too short to contain nonce + tag.");
        }

        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var ciphertext = new byte[combined.Length - NonceSize - TagSize];

        Buffer.BlockCopy(combined, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(combined, NonceSize, ciphertext, 0, ciphertext.Length);
        Buffer.BlockCopy(combined, NonceSize + ciphertext.Length, tag, 0, TagSize);

        var plaintextBytes = new byte[ciphertext.Length];
        using var aes = new AesGcm(_key, TagSize);
        // AuthenticationTagMismatchException bubbles when the ciphertext or tag has been tampered with.
        aes.Decrypt(nonce, ciphertext, tag, plaintextBytes);
        return System.Text.Encoding.UTF8.GetString(plaintextBytes);
    }
}

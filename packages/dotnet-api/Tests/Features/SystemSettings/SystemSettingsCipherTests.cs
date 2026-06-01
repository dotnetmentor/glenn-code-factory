using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Source.Features.SystemSettings.Services;

namespace Api.Tests.Features.SystemSettings;

/// <summary>
/// Unit tests for <see cref="SystemSettingsCipher"/>. Covers round-trip,
/// tamper detection (AES-GCM auth-tag), and nonce uniqueness.
/// </summary>
public class SystemSettingsCipherTests
{
    private static SystemSettingsCipher BuildCipher()
    {
        // Static base64-encoded 32-byte key — deterministic across the suite.
        var keyBytes = new byte[32];
        for (var i = 0; i < keyBytes.Length; i++) keyBytes[i] = (byte)i;
        var keyB64 = Convert.ToBase64String(keyBytes);

        return new SystemSettingsCipher(
            Options.Create(new SystemSettingsCipherOptions { EncryptionKey = keyB64 }));
    }

    [Fact]
    public void Encrypt_then_Decrypt_returns_original_plaintext()
    {
        var cipher = BuildCipher();
        const string plaintext = "hunter2";

        var encrypted = cipher.Encrypt(plaintext);
        var decrypted = cipher.Decrypt(encrypted);

        decrypted.Should().Be(plaintext);
        encrypted.Should().NotBe(plaintext);
    }

    [Fact]
    public void Decrypt_throws_when_ciphertext_is_tampered_with()
    {
        var cipher = BuildCipher();
        var encrypted = cipher.Encrypt("important-secret-value");

        // Decode, flip a byte in the middle (definitely inside the ciphertext, not nonce/tag),
        // re-encode, and try to decrypt — auth tag must reject it.
        var bytes = Convert.FromBase64String(encrypted);
        bytes[bytes.Length / 2] ^= 0x01;
        var tampered = Convert.ToBase64String(bytes);

        var act = () => cipher.Decrypt(tampered);
        act.Should().Throw<CryptographicException>(); // AuthenticationTagMismatchException : CryptographicException
    }

    [Fact]
    public void Encrypt_produces_different_ciphertexts_for_the_same_plaintext()
    {
        // Nonce uniqueness — every Encrypt call must use a fresh random nonce, so
        // back-to-back calls with identical plaintext yield different ciphertexts.
        var cipher = BuildCipher();
        const string plaintext = "same-input";

        var a = cipher.Encrypt(plaintext);
        var b = cipher.Encrypt(plaintext);

        a.Should().NotBe(b);
        cipher.Decrypt(a).Should().Be(plaintext);
        cipher.Decrypt(b).Should().Be(plaintext);
    }

    [Fact]
    public void Constructor_throws_when_key_is_missing()
    {
        var act = () => new SystemSettingsCipher(
            Options.Create(new SystemSettingsCipherOptions { EncryptionKey = "" }));
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not configured*");
    }

    [Fact]
    public void Constructor_throws_when_key_is_wrong_length()
    {
        // 16 bytes — half the required size.
        var keyB64 = Convert.ToBase64String(new byte[16]);
        var act = () => new SystemSettingsCipher(
            Options.Create(new SystemSettingsCipherOptions { EncryptionKey = keyB64 }));
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*must decode to exactly 32 bytes*");
    }
}

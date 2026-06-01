using System.Security.Cryptography;
using System.Text;
using Source.Features.GitHub.Configuration;
using Source.Features.GitHub.Services;

namespace Api.Tests.Features.GitHub;

/// <summary>
/// Unit tests for <see cref="GithubWebhookValidator"/>.
/// Covers happy path, mismatch, missing/malformed headers and body-mutation.
/// </summary>
public class GithubWebhookValidatorTests
{
    private const string Secret = "test-secret";

    private static GithubWebhookValidator Build(string secret = Secret) =>
        new(new StubGithubOptionsAccessor(new GithubOptions { WebhookSecret = secret }));

    private static string SignBody(byte[] body, string secret = Secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var bytes = hmac.ComputeHash(body);
        return "sha256=" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    [Fact]
    public void Validate_returns_true_for_matching_signature()
    {
        var body = Encoding.UTF8.GetBytes(@"{""event"":""push""}");
        var sig = SignBody(body);

        var validator = Build();
        validator.Validate(sig, body).Should().BeTrue();
    }

    [Fact]
    public void Validate_returns_false_for_mismatched_signature()
    {
        var body = Encoding.UTF8.GetBytes(@"{""event"":""push""}");
        // Sign with a different secret — same shape but wrong HMAC.
        var sig = SignBody(body, secret: "wrong-secret");

        var validator = Build();
        validator.Validate(sig, body).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-prefixed")]
    [InlineData("sha256=")]              // prefix only
    [InlineData("sha1=abcdef")]           // wrong algo prefix
    [InlineData("sha256=zz")]             // non-hex characters
    [InlineData("sha256=abc")]            // odd-length hex
    public void Validate_returns_false_for_missing_or_malformed_header(string? header)
    {
        var body = Encoding.UTF8.GetBytes("{}");
        var validator = Build();
        validator.Validate(header, body).Should().BeFalse();
    }

    [Fact]
    public void Validate_returns_false_when_body_is_mutated_by_one_byte()
    {
        var body = Encoding.UTF8.GetBytes(@"{""event"":""push"",""ref"":""main""}");
        var sig = SignBody(body);

        // Flip the very first byte of the body.
        var mutated = (byte[])body.Clone();
        mutated[0] ^= 0x01;

        var validator = Build();
        validator.Validate(sig, mutated).Should().BeFalse();
    }

    [Fact]
    public void Validate_returns_false_when_secret_is_not_configured()
    {
        var body = Encoding.UTF8.GetBytes("{}");
        var sig = SignBody(body);
        var validator = Build(secret: "");
        validator.Validate(sig, body).Should().BeFalse();
    }

    [Fact]
    public void Validate_is_case_insensitive_on_the_sha256_prefix()
    {
        var body = Encoding.UTF8.GetBytes("{}");
        var sig = SignBody(body).Replace("sha256=", "SHA256=");

        var validator = Build();
        validator.Validate(sig, body).Should().BeTrue();
    }
}

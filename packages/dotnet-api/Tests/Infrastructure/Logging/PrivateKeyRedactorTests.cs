using Source.Infrastructure.Logging;

namespace Api.Tests.Infrastructure.Logging;

/// <summary>
/// Pure-function coverage for <see cref="PrivateKeyRedactor"/>. Sibling to
/// <see cref="JwtRedactorTests"/>; same shape, same caller surface.
/// </summary>
public class PrivateKeyRedactorTests
{
    private const string SampleEd25519 = """
        -----BEGIN OPENSSH PRIVATE KEY-----
        b3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQAAAAAAAAABAAAAMwAAAAtzc2gtZW
        QyNTUxOQAAACAabcdefGHIJKLmnopQRSTuvwxYZ0123456789AAAAAAAAAAA==
        -----END OPENSSH PRIVATE KEY-----
        """;

    private const string SampleRsa = """
        -----BEGIN RSA PRIVATE KEY-----
        MIIEpAIBAAKCAQEAblahblahblahFAKEbodyFAKEbodyFAKEbody==
        -----END RSA PRIVATE KEY-----
        """;

    [Fact]
    public void Redact_RealEd25519Key_IsScrubbed()
    {
        var input = $"connecting with key:\n{SampleEd25519}\ndone";

        var output = PrivateKeyRedactor.Redact(input);

        output.Should().Contain("[REDACTED PRIVATE KEY]");
        output.Should().NotContain("BEGIN OPENSSH");
        output.Should().NotContain("b3BlbnNzaC1rZXk");
    }

    [Fact]
    public void Redact_PlainTextWithoutKey_IsUntouched()
    {
        const string input = "User logged in successfully";

        var output = PrivateKeyRedactor.Redact(input);

        output.Should().Be(input);
    }

    [Fact]
    public void Redact_MultipleKeysInOneMessage_AllScrubbed()
    {
        // Lazy match (.*?) ensures we don't over-eat from BEGIN of the first
        // key all the way to END of the last key.
        var input = $"first:\n{SampleEd25519}\nsecond:\n{SampleRsa}\ndone";

        var output = PrivateKeyRedactor.Redact(input);

        output.Should().NotContain("BEGIN OPENSSH");
        output.Should().NotContain("BEGIN RSA");
        // Both replacements present.
        output.Split("[REDACTED PRIVATE KEY]").Length.Should().Be(3,
            "two keys mean two replacements; split-by gives n+1 chunks");
    }

    [Fact]
    public void Redact_NullInput_ReturnsEmptyStringWithoutThrowing()
    {
        PrivateKeyRedactor.Redact(null).Should().Be(string.Empty);
    }

    [Fact]
    public void Redact_EmptyInput_ReturnsEmptyString()
    {
        PrivateKeyRedactor.Redact(string.Empty).Should().Be(string.Empty);
    }

    [Theory]
    // Header without matching footer.
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----\nbody")]
    // Footer without header.
    [InlineData("body\n-----END OPENSSH PRIVATE KEY-----")]
    // Word "PRIVATE" without the dashes envelope.
    [InlineData("This is my PRIVATE KEY but not the PEM kind.")]
    public void Redact_StringsThatLookSimilarButDoNotMatch_AreNotRedacted(string input)
    {
        var output = PrivateKeyRedactor.Redact(input);
        output.Should().Be(input, "over-eager scrubbing would corrupt unrelated logs");
    }

    [Fact]
    public void ContainsMatch_RealKey_ReturnsTrue()
    {
        PrivateKeyRedactor.ContainsMatch($"prefix\n{SampleEd25519}\nsuffix").Should().BeTrue();
    }

    [Fact]
    public void ContainsMatch_PlainText_ReturnsFalse()
    {
        PrivateKeyRedactor.ContainsMatch("nothing to see here").Should().BeFalse();
    }

    [Fact]
    public void ContainsMatch_NullOrEmpty_ReturnsFalse()
    {
        PrivateKeyRedactor.ContainsMatch(null).Should().BeFalse();
        PrivateKeyRedactor.ContainsMatch(string.Empty).Should().BeFalse();
    }
}

using Source.Infrastructure.Logging;

namespace Api.Tests.Infrastructure.Logging;

/// <summary>
/// Pure-function coverage for <see cref="JwtRedactor"/>. The redactor is the
/// single source of truth for the pattern + replacement; everything else
/// (logger provider decorator) calls into these methods.
/// </summary>
public class JwtRedactorTests
{
    // A real-shaped JWT — header.payload.signature, all base64url, three segments.
    // Header decodes to {"alg":"HS256","typ":"JWT"}; payload to {"sub":"1234"}.
    private const string SampleJwt =
        "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0In0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";

    [Fact]
    public void Redact_RealJwtInLongerString_IsScrubbed()
    {
        var input = $"Authorization: Bearer {SampleJwt} done";

        var output = JwtRedactor.Redact(input);

        output.Should().Be("Authorization: Bearer eyJ***REDACTED*** done");
    }

    [Fact]
    public void Redact_PlainTextWithoutJwt_IsUntouched()
    {
        const string input = "User logged in successfully";

        var output = JwtRedactor.Redact(input);

        output.Should().Be(input);
    }

    [Fact]
    public void Redact_MultipleJwtsInOneMessage_AllScrubbed()
    {
        var input = $"first={SampleJwt} second={SampleJwt}";

        var output = JwtRedactor.Redact(input);

        output.Should().Be("first=eyJ***REDACTED*** second=eyJ***REDACTED***");
    }

    [Fact]
    public void Redact_NullInput_ReturnsEmptyStringWithoutThrowing()
    {
        var output = JwtRedactor.Redact(null);

        output.Should().Be(string.Empty);
    }

    [Fact]
    public void Redact_EmptyInput_ReturnsEmptyString()
    {
        var output = JwtRedactor.Redact(string.Empty);

        output.Should().Be(string.Empty);
    }

    [Theory]
    // Starts with eyJ but only one dot — not a JWT.
    [InlineData("eyJabc.def")]
    // Three segments but first doesn't start with eyJ.
    [InlineData("abc.def.ghi")]
    // Has invalid characters (spaces) breaking the segment.
    [InlineData("eyJabc def.ghi.jkl")]
    // Just the prefix, no segments.
    [InlineData("eyJ")]
    // A token-like structure with an empty middle segment is rejected by [A-Za-z0-9_-]+ which requires 1+.
    [InlineData("eyJabc..ghi")]
    public void Redact_StringsThatLookSimilarButDoNotMatch_AreNotRedacted(string input)
    {
        var output = JwtRedactor.Redact(input);

        output.Should().Be(input, "over-eager scrubbing would corrupt unrelated logs");
    }

    [Fact]
    public void ContainsMatch_RealJwt_ReturnsTrue()
    {
        JwtRedactor.ContainsMatch($"prefix {SampleJwt} suffix").Should().BeTrue();
    }

    [Fact]
    public void ContainsMatch_PlainText_ReturnsFalse()
    {
        JwtRedactor.ContainsMatch("nothing to see here").Should().BeFalse();
    }

    [Fact]
    public void ContainsMatch_NullOrEmpty_ReturnsFalse()
    {
        JwtRedactor.ContainsMatch(null).Should().BeFalse();
        JwtRedactor.ContainsMatch(string.Empty).Should().BeFalse();
    }
}

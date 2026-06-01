using Source.Infrastructure.Logging;

namespace Api.Tests.Infrastructure.Logging;

/// <summary>
/// Pure-function coverage for <see cref="SecretValueRedactor"/>. Sibling to
/// <see cref="JwtRedactorTests"/> and <see cref="PrivateKeyRedactorTests"/>;
/// same shape, same caller surface.
///
/// The mirror test file on the daemon side
/// (<c>packages/daemon/src/logging/secretRedactor.test.ts</c>) carries the same
/// case set so coverage stays in lockstep across runtimes.
/// </summary>
public class SecretValueRedactorTests
{
    // ------------------------------------------------------------------------
    // Positive cases — each enabled regex matches at least one example.
    // ------------------------------------------------------------------------

    [Fact]
    public void Redact_StripeSecretKey_IsScrubbed()
    {
        const string input = "Charge failed key=sk_live_abcdefghij1234567890XYZ end";

        var output = SecretValueRedactor.Redact(input);

        output.Should().Contain(SecretValueRedactor.Replacement);
        output.Should().NotContain("sk_live_abcdefghij1234567890XYZ");
    }

    [Fact]
    public void Redact_StripeTestSecretKey_IsScrubbed()
    {
        const string input = "Using sk_test_abcdefghij1234567890XYZ to charge.";

        var output = SecretValueRedactor.Redact(input);

        output.Should().NotContain("sk_test_abcdefghij1234567890XYZ");
        output.Should().Contain(SecretValueRedactor.Replacement);
    }

    [Fact]
    public void Redact_StripePublishableKey_IsScrubbed()
    {
        const string input = "frontend uses pk_live_abcdefghij1234567890ABCDEF";

        var output = SecretValueRedactor.Redact(input);

        output.Should().NotContain("pk_live_abcdefghij1234567890ABCDEF");
        output.Should().Contain(SecretValueRedactor.Replacement);
    }

    [Fact]
    public void Redact_BearerTokenInHeader_IsScrubbed()
    {
        // The whole "Bearer X" run is replaced by the marker — mirrors how
        // JwtRedactor swallows the entire token (no preserved prefix). This is
        // the documented behaviour in SecretValueRedactor's class doc.
        const string input = "Authorization: Bearer abcdefghij1234567890ZZZZZ done";

        var output = SecretValueRedactor.Redact(input);

        output.Should().NotContain("abcdefghij1234567890ZZZZZ");
        output.Should().Contain(SecretValueRedactor.Replacement);
    }

    [Fact]
    public void Redact_BearerTokenIsCaseInsensitive()
    {
        const string input = "authorization: bearer abcdefghij1234567890ZZZZZ";

        var output = SecretValueRedactor.Redact(input);

        output.Should().NotContain("abcdefghij1234567890ZZZZZ");
    }

    [Fact]
    public void Redact_AwsAccessKey_IsScrubbed()
    {
        const string input = "uploading via AKIAIOSFODNN7EXAMPLE to bucket";

        var output = SecretValueRedactor.Redact(input);

        output.Should().NotContain("AKIAIOSFODNN7EXAMPLE");
        output.Should().Contain(SecretValueRedactor.Replacement);
    }

    [Fact]
    public void Redact_OpenAIKey_IsScrubbed()
    {
        const string input = "key=sk-abcdefghij1234567890XYZab end";

        var output = SecretValueRedactor.Redact(input);

        output.Should().NotContain("sk-abcdefghij1234567890XYZab");
        output.Should().Contain(SecretValueRedactor.Replacement);
    }

    [Fact]
    public void Redact_OpenAIProjectKey_IsScrubbed()
    {
        const string input = "OPENAI_API_KEY=sk-proj-abcdefghij1234567890XYZab";

        var output = SecretValueRedactor.Redact(input);

        output.Should().NotContain("sk-proj-abcdefghij1234567890XYZab");
        output.Should().Contain(SecretValueRedactor.Replacement);
    }

    [Fact]
    public void Redact_MultipleSecretsInOneLine_AllScrubbed()
    {
        const string input =
            "stripe=sk_live_abcdefghij1234567890XYZ aws=AKIAIOSFODNN7EXAMPLE";

        var output = SecretValueRedactor.Redact(input);

        output.Should().NotContain("sk_live_abcdefghij1234567890XYZ");
        output.Should().NotContain("AKIAIOSFODNN7EXAMPLE");
        // Two distinct redactions present.
        output.Split(SecretValueRedactor.Replacement).Length.Should().Be(3,
            "two secrets mean two replacements; split-by gives n+1 chunks");
    }

    // ------------------------------------------------------------------------
    // Negative cases — strings that look secret-shaped but aren't.
    // ------------------------------------------------------------------------

    [Fact]
    public void Redact_Uuid_IsNotScrubbedByDefault()
    {
        // A real UUID — would match GenericHighEntropy length-wise but only
        // if hyphens were allowed-AND-counted. Our pattern requires 40+ chars
        // continuous in [A-Za-z0-9+/=_-]; UUID hyphens split it into shorter
        // runs. The default set excludes GenericHighEntropy entirely, so the
        // string passes through untouched.
        const string input = "request_id=550e8400-e29b-41d4-a716-446655440000 succeeded";

        var output = SecretValueRedactor.Redact(input);

        output.Should().Be(input, "UUIDs must not collide with secret patterns");
    }

    [Fact]
    public void Redact_NormalLogMessage_IsUntouched()
    {
        const string input = "User alice@example.com logged in at 12:34:56";

        var output = SecretValueRedactor.Redact(input);

        output.Should().Be(input);
    }

    [Fact]
    public void Redact_ShortStripeLikeString_IsNotScrubbed()
    {
        // Shorter than the 20-char minimum — the regex requires {20,} after
        // the prefix to dodge false-positives on phrases like "sk_test_x" used
        // as documentation placeholders.
        const string input = "example: sk_test_short";

        var output = SecretValueRedactor.Redact(input);

        output.Should().Be(input);
    }

    [Fact]
    public void Redact_NullInput_ReturnsEmptyString()
    {
        SecretValueRedactor.Redact(null).Should().Be(string.Empty);
    }

    [Fact]
    public void Redact_EmptyInput_ReturnsEmptyString()
    {
        SecretValueRedactor.Redact(string.Empty).Should().Be(string.Empty);
    }

    [Fact]
    public void ContainsMatch_StripeKey_ReturnsTrue()
    {
        SecretValueRedactor
            .ContainsMatch("prefix sk_live_abcdefghij1234567890XYZ suffix")
            .Should().BeTrue();
    }

    [Fact]
    public void ContainsMatch_PlainText_ReturnsFalse()
    {
        SecretValueRedactor.ContainsMatch("nothing to see here").Should().BeFalse();
    }

    [Fact]
    public void ContainsMatch_NullOrEmpty_ReturnsFalse()
    {
        SecretValueRedactor.ContainsMatch(null).Should().BeFalse();
        SecretValueRedactor.ContainsMatch(string.Empty).Should().BeFalse();
    }

    // ------------------------------------------------------------------------
    // Opt-in: GenericHighEntropy
    // ------------------------------------------------------------------------

    [Fact]
    public void Redact_WithHighEntropyOptIn_ScrubsLongOpaqueStrings()
    {
        // 48 chars of base64-ish noise — the kind of leak this catches when
        // operators have explicitly opted in.
        const string input = "token=abcdefghijklmnopqrstuvwxyzABCDEFGHIJ0123456789xx done";

        var output = SecretValueRedactor.Redact(input, includeHighEntropy: true);

        output.Should().Contain(SecretValueRedactor.Replacement);
        output.Should().NotContain("abcdefghijklmnopqrstuvwxyzABCDEFGHIJ0123456789xx");
    }

    [Fact]
    public void Redact_WithoutHighEntropyOptIn_PassesLongOpaqueStringsThrough()
    {
        // Same input as above — without the opt-in, the string is left alone
        // because the false-positive risk on real logs is too high.
        const string input = "build_hash=abcdefghijklmnopqrstuvwxyzABCDEFGHIJ0123456789xx";

        var output = SecretValueRedactor.Redact(input);

        output.Should().Be(input,
            "GenericHighEntropy is disabled by default to avoid scrubbing UUIDs/hashes");
    }
}

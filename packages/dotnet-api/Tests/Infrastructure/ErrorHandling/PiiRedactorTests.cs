using Source.Infrastructure.ErrorHandling;

namespace Api.Tests.Infrastructure.ErrorHandling;

/// <summary>
/// Table-driven tests for <see cref="PiiRedactor"/>. Exercises each of the 6 redaction
/// patterns in isolation and verifies idempotency + no-op on safe input.
/// </summary>
public class PiiRedactorTests
{
    private readonly IPiiRedactor _redactor = new PiiRedactor();

    // --- Email ---

    [Theory]
    [InlineData("contact alice@example.com for help", "<email>", "alice@")]
    [InlineData("user+tag@sub.domain.co.uk is valid", "<email>", "user+tag@")]
    public void Redact_Email_ReplacesWithPlaceholder(string input, string expectedSubstring, string forbiddenSubstring)
    {
        var result = _redactor.Redact(input);

        result.Should().Contain(expectedSubstring);
        result.Should().NotContain(forbiddenSubstring);
    }

    [Fact]
    public void Redact_MultipleEmails_ReplacesAll()
    {
        var result = _redactor.Redact("from a@b.com to c@d.com");

        result.Should().NotContain("a@b.com");
        result.Should().NotContain("c@d.com");
        result.Should().Contain("<email>");
    }

    // --- Bearer token ---

    [Theory]
    [InlineData("Authorization: Bearer abc123.def456", "Bearer <redacted>")]
    [InlineData("use Bearer token-value_here+=", "Bearer <redacted>")]
    public void Redact_BearerToken_ReplacesWithPlaceholder(string input, string expectedSubstring)
    {
        var result = _redactor.Redact(input);

        result.Should().Contain(expectedSubstring);
    }

    // --- JWT ---

    [Fact]
    public void Redact_Jwt_ReplacesWithPlaceholder()
    {
        const string input = "jwt=eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NSJ9.abcdefghij";

        var result = _redactor.Redact(input);

        result.Should().Contain("<jwt>");
        result.Should().NotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9");
    }

    // --- Key-value secrets ---

    [Theory]
    [InlineData("password=secret123;next", "password=<redacted>", "secret123")]
    [InlineData("APIKEY=xyz abc", "APIKEY=<redacted>", "xyz")]
    [InlineData("api_key=ABCD-1234&other=ok", "api_key=<redacted>", "ABCD-1234")]
    [InlineData("secret=topsecret;", "secret=<redacted>", "topsecret")]
    [InlineData("token=tttttt", "token=<redacted>", "tttttt")]
    public void Redact_KeyValueSecret_ReplacesWithPlaceholderPreservingKey(
        string input, string expectedSubstring, string forbiddenValue)
    {
        var result = _redactor.Redact(input);

        result.Should().Contain(expectedSubstring);
        result.Should().NotContain(forbiddenValue);
    }

    // --- Postgres connection string ---

    [Theory]
    [InlineData("Host=db;Port=5432;Database=x;Username=u;Password=p")]
    [InlineData("Server=prod.rds.amazonaws.com;Database=app;Username=svc;Password=hunter2;Port=5432")]
    public void Redact_ConnectionString_ReplacesWholeMatchWithConnstring(string input)
    {
        var result = _redactor.Redact(input);

        result.Should().Contain("<connstring>");
        // Individual kv-secret pattern must NOT fire inside the connstring match
        result.Should().NotContain("Password=p");
        result.Should().NotContain("Password=hunter2");
    }

    // --- Credit card ---

    [Fact]
    public void Redact_SixteenDigitNumber_ReplacesWithCardPlaceholder()
    {
        var result = _redactor.Redact("4111111111111111");

        result.Should().Contain("<card>");
        result.Should().NotContain("4111111111111111");
    }

    [Fact]
    public void Redact_SixteenDigitWithSpaces_IsUnchanged_DocumentedTradeOff()
    {
        // Trade-off: the spec specifies \b\d{16}\b — spaced variants are not matched.
        const string input = "4111 1111 1111 1111";

        var result = _redactor.Redact(input);

        result.Should().Be(input);
    }

    // --- Null / empty / no-op ---

    [Fact]
    public void Redact_Null_ReturnsNull()
    {
        _redactor.Redact(null).Should().BeNull();
    }

    [Fact]
    public void Redact_Empty_ReturnsEmpty()
    {
        _redactor.Redact(string.Empty).Should().Be(string.Empty);
    }

    [Fact]
    public void Redact_NoPii_ReturnsInputByteForByte()
    {
        const string input = "hello world";

        var result = _redactor.Redact(input);

        result.Should().Be(input);
    }

    // --- Idempotency ---

    [Fact]
    public void Redact_AppliedTwice_IsIdempotent()
    {
        const string input = "contact alice@example.com, token=abc123, Bearer xyz.def, Password=hunter2";

        var once = _redactor.Redact(input);
        var twice = _redactor.Redact(once);

        twice.Should().Be(once);
    }
}

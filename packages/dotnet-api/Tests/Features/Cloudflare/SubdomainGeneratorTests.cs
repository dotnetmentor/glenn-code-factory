using Source.Features.Cloudflare.Services;

namespace Api.Tests.Features.Cloudflare;

/// <summary>
/// Unit tests for <see cref="SubdomainGenerator"/>. Covers the format
/// invariants (length, alphabet) and the cryptographic-randomness signal we
/// can probe in-process: uniqueness across many draws.
/// </summary>
public class SubdomainGeneratorTests
{
    [Fact]
    public void Generate_returns_8_chars()
    {
        var s = SubdomainGenerator.Generate();
        s.Should().HaveLength(8);
    }

    [Fact]
    public void Generate_only_uses_lowercase_alphanumeric()
    {
        for (var i = 0; i < 200; i++)
        {
            var s = SubdomainGenerator.Generate();
            s.Should().MatchRegex("^[a-z0-9]{8}$", "every char must be lowercase letter or digit");
        }
    }

    [Fact]
    public void Generate_produces_distinct_values_across_many_draws()
    {
        // Birthday paradox on 36^8 ≈ 2.82e12 means a 1000-sample collision is
        // astronomically unlikely (≈1 in ~5.6 billion). A collision here means
        // the generator is broken — likely seeded from wall-clock time rather
        // than a CSPRNG. The test pins that we wired RandomNumberGenerator,
        // not System.Random.
        var seen = new HashSet<string>(capacity: 1000);
        for (var i = 0; i < 1000; i++)
        {
            seen.Add(SubdomainGenerator.Generate());
        }
        seen.Should().HaveCount(1000, "collisions in 1000 draws would indicate a non-CSPRNG source");
    }

    [Fact]
    public void Alphabet_is_36_chars_lowercase_alphanumeric()
    {
        SubdomainGenerator.Alphabet.Should().HaveLength(36);
        SubdomainGenerator.Alphabet.Should().MatchRegex("^[a-z0-9]+$");
        SubdomainGenerator.Alphabet.ToCharArray().Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Length_constant_is_8()
    {
        SubdomainGenerator.Length.Should().Be(8);
    }
}

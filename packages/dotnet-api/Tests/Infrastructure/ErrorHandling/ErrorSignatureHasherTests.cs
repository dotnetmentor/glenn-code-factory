using System.Text.RegularExpressions;
using Source.Infrastructure.ErrorHandling;

namespace Api.Tests.Infrastructure.ErrorHandling;

/// <summary>
/// Tests for <see cref="ErrorSignatureHasher"/>. Covers determinism, stability across
/// unrelated frames, fallback on null stack, never-throws guarantee, and the 64-hex-char
/// output contract.
/// </summary>
public class ErrorSignatureHasherTests
{
    private readonly IErrorSignatureHasher _hasher = new ErrorSignatureHasher();

    private static readonly Regex HexRegex = new("^[0-9a-f]{64}$", RegexOptions.Compiled);

    private static ErrorEntry MakeEntry(
        string message,
        string? stackTrace,
        string source = "HTTP",
        string severity = "Error") =>
        new(
            Message: message,
            StackTrace: stackTrace,
            Source: source,
            Severity: severity,
            CorrelationId: null,
            RequestPath: null,
            RequestMethod: null,
            ContextData: null,
            OccurredAt: DateTime.UtcNow);

    private const string StackA =
        "System.InvalidOperationException: boom\n" +
        "   at MyApp.Domain.Orders.OrderService.Place(Guid id) in /src/Orders/OrderService.cs:line 42\n" +
        "   at MyApp.Api.OrdersController.Post(PlaceRequest req) in /src/Api/OrdersController.cs:line 17\n" +
        "   at MyApp.Infrastructure.Pipeline.Invoke(HttpContext ctx) in /src/Infra/Pipeline.cs:line 88\n" +
        "   at MyApp.Program.Main(string[] args) in /src/Program.cs:line 5";

    private const string StackB =
        "System.InvalidOperationException: boom\n" +
        "   at MyApp.Domain.Orders.OrderService.Place(Guid id) in /src/Orders/OrderService.cs:line 42\n" +
        "   at MyApp.Api.OrdersController.Post(PlaceRequest req) in /src/Api/OrdersController.cs:line 17\n" +
        "   at MyApp.Infrastructure.Pipeline.Invoke(HttpContext ctx) in /src/Infra/Pipeline.cs:line 88\n" +
        "   at SomeOther.Unrelated.Method() in /src/Other.cs:line 999\n" +
        "   at Yet.Another.Frame() in /src/More.cs:line 12345";

    [Fact]
    public void SameTypeAndTopThreeFrames_ProducesSameHash()
    {
        var a = MakeEntry("boom", StackA);
        var b = MakeEntry("boom", StackA);

        _hasher.Hash(a).Should().Be(_hasher.Hash(b));
    }

    [Fact]
    public void DifferentExceptionTypes_ProduceDifferentHashes()
    {
        var stack1 =
            "System.InvalidOperationException: x\n" +
            "   at MyApp.A.B.C() in /a.cs:line 1\n" +
            "   at MyApp.D.E.F() in /b.cs:line 2\n" +
            "   at MyApp.G.H.I() in /c.cs:line 3";
        var stack2 =
            "System.ArgumentNullException: x\n" +
            "   at MyApp.A.B.C() in /a.cs:line 1\n" +
            "   at MyApp.D.E.F() in /b.cs:line 2\n" +
            "   at MyApp.G.H.I() in /c.cs:line 3";

        var a = MakeEntry("x", stack1);
        var b = MakeEntry("x", stack2);

        _hasher.Hash(a).Should().NotBe(_hasher.Hash(b));
    }

    [Fact]
    public void DifferentTopThreeFrames_ProduceDifferentHashes()
    {
        var stack1 =
            "System.InvalidOperationException: x\n" +
            "   at MyApp.First.Method() in /a.cs:line 1\n" +
            "   at MyApp.Second.Method() in /b.cs:line 2\n" +
            "   at MyApp.Third.Method() in /c.cs:line 3";
        var stack2 =
            "System.InvalidOperationException: x\n" +
            "   at Totally.Different.Method() in /x.cs:line 1\n" +
            "   at Another.Frame.Method() in /y.cs:line 2\n" +
            "   at Third.Differs.Method() in /z.cs:line 3";

        _hasher.Hash(MakeEntry("x", stack1))
            .Should()
            .NotBe(_hasher.Hash(MakeEntry("x", stack2)));
    }

    [Fact]
    public void NullStackTrace_ProducesHashFromTypeAndMessagePrefix()
    {
        var entry = MakeEntry("something bad happened", stackTrace: null);

        var hash1 = _hasher.Hash(entry);
        var hash2 = _hasher.Hash(entry);

        hash1.Should().MatchRegex("^[0-9a-f]{64}$");
        hash1.Should().Be(hash2);
    }

    [Fact]
    public void EmptyStackTrace_ProducesHashFromTypeAndMessagePrefix()
    {
        var entry = MakeEntry("something bad happened", stackTrace: string.Empty);

        var hash = _hasher.Hash(entry);

        hash.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void FramesBeyondTopThreeDiffer_SameHash()
    {
        // StackA has 4 frames, StackB has identical top 3 but differs at frame 4+.
        _hasher.Hash(MakeEntry("boom", StackA))
            .Should()
            .Be(_hasher.Hash(MakeEntry("boom", StackB)));
    }

    [Fact]
    public void FrameThreeDiffers_DifferentHash()
    {
        var stack1 =
            "System.InvalidOperationException: x\n" +
            "   at MyApp.First.Method() in /a.cs:line 1\n" +
            "   at MyApp.Second.Method() in /b.cs:line 2\n" +
            "   at MyApp.Third.Method() in /c.cs:line 3";
        var stack2 =
            "System.InvalidOperationException: x\n" +
            "   at MyApp.First.Method() in /a.cs:line 1\n" +
            "   at MyApp.Second.Method() in /b.cs:line 2\n" +
            "   at MyApp.Third.DIFFERENT() in /c.cs:line 3";

        _hasher.Hash(MakeEntry("x", stack1))
            .Should()
            .NotBe(_hasher.Hash(MakeEntry("x", stack2)));
    }

    [Fact]
    public void OutputIsAlways64HexChars()
    {
        var samples = new[]
        {
            MakeEntry("a", null),
            MakeEntry("b", ""),
            MakeEntry("c", "System.Exception: x"),
            MakeEntry("d", StackA),
            MakeEntry("e", StackB),
            MakeEntry("f", "not a real stack"),
            MakeEntry("g", "   at X.Y.Z()"),
            MakeEntry("", null),
            MakeEntry("message with unicode: 日本語 🚀", StackA),
            MakeEntry("x", StackA, source: "Frontend"),
            MakeEntry("x", StackA, source: "Handler"),
            MakeEntry("x", StackA, severity: "Critical"),
            MakeEntry(new string('m', 1000), null),
            MakeEntry("one-frame", "System.Exception: x\n   at A.B.C() in /a.cs:line 1"),
            MakeEntry("two-frames", "System.Exception: x\n   at A.B.C() in /a.cs:line 1\n   at D.E.F() in /b.cs:line 2"),
            MakeEntry("no-in-clause", "System.Exception: x\n   at A.B.C()\n   at D.E.F()\n   at G.H.I()"),
            MakeEntry("colon-in-message", "System.Exception: error: nested: colons\n   at A.B.C() in /a.cs:line 1"),
            MakeEntry("short", "System.Exception"),
            MakeEntry("malformed", "complete garbage without format"),
            MakeEntry("tabs and whitespace", "\t  \r\n  "),
        };

        foreach (var entry in samples)
        {
            var hash = _hasher.Hash(entry);
            hash.Should().NotBeNull();
            hash.Should().MatchRegex("^[0-9a-f]{64}$");
        }
    }

    [Fact]
    public void SameInputCalledManyTimes_AlwaysSameOutput()
    {
        var entry = MakeEntry("deterministic", StackA);
        var expected = _hasher.Hash(entry);

        for (var i = 0; i < 100; i++)
        {
            _hasher.Hash(entry).Should().Be(expected);
        }
    }

    [Fact]
    public void RandomizedNeverThrows_AlwaysValidFormat()
    {
        var rng = new Random(12345);

        for (var i = 0; i < 100; i++)
        {
            var entry = RandomEntry(rng);

            string? hash = null;
            var act = () => hash = _hasher.Hash(entry);

            act.Should().NotThrow();
            hash.Should().NotBeNull();
            HexRegex.IsMatch(hash!).Should().BeTrue($"iteration {i} produced '{hash}'");
        }
    }

    [Fact]
    public void DifferentSourcesProduceDifferentHashes()
    {
        // Decision (documented in ErrorSignatureHasher): Source is part of the hash
        // so Frontend and HTTP errors with identical stacks don't collapse.
        var a = MakeEntry("boom", StackA, source: "HTTP");
        var b = MakeEntry("boom", StackA, source: "Frontend");

        _hasher.Hash(a).Should().NotBe(_hasher.Hash(b));
    }

    private static ErrorEntry RandomEntry(Random rng)
    {
        string? nullable() => rng.Next(4) == 0 ? null : RandomString(rng, 0, 300);

        return new ErrorEntry(
            Message: rng.Next(10) == 0 ? string.Empty : RandomString(rng, 0, 500),
            StackTrace: rng.Next(5) == 0 ? null : RandomStackTrace(rng),
            Source: RandomPick(rng, "HTTP", "Handler", "Hangfire", "Database", "Frontend", ""),
            Severity: RandomPick(rng, "Error", "Warning", "Critical"),
            CorrelationId: nullable(),
            RequestPath: nullable(),
            RequestMethod: nullable(),
            ContextData: nullable(),
            OccurredAt: DateTime.UtcNow);
    }

    private static string RandomPick(Random rng, params string[] choices) =>
        choices[rng.Next(choices.Length)];

    private static string RandomString(Random rng, int min, int max)
    {
        var len = rng.Next(min, max);
        var buf = new char[len];
        for (var i = 0; i < len; i++)
        {
            // Broad char set including control chars, newlines, unicode.
            buf[i] = (char)rng.Next(0, 0x1000);
        }
        return new string(buf);
    }

    private static string RandomStackTrace(Random rng)
    {
        var frames = rng.Next(0, 8);
        var lines = new List<string>
        {
            $"Random.Exception.Type{rng.Next(100)}: {RandomString(rng, 0, 40)}"
        };
        for (var i = 0; i < frames; i++)
        {
            // Mix well-formed and malformed frames.
            if (rng.Next(4) == 0)
            {
                lines.Add("   at garbage without structure " + RandomString(rng, 0, 20));
            }
            else
            {
                lines.Add($"   at Ns{i}.Type{i}.Method{i}(args) in /f/{i}.cs:line {rng.Next(1, 10000)}");
            }
        }
        return string.Join("\n", lines);
    }
}

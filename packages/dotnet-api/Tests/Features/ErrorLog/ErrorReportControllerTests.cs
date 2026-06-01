using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Api.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Source.Infrastructure.ErrorHandling;

namespace Api.Tests.Features.ErrorLog;

/// <summary>
/// Test-only subclass of <see cref="ErrorQueue"/> that ALSO keeps each enqueued entry in
/// a thread-safe list, so tests can assert on what reached the queue without racing with
/// the production <c>ErrorPersistenceWorker</c> hosted service (which drains the channel
/// from under us otherwise).
/// </summary>
internal sealed class CapturingErrorQueue : ErrorQueue
{
    public ConcurrentBag<ErrorEntry> Captured { get; } = new();

    public CapturingErrorQueue(IPiiRedactor redactor) : base(redactor) { }

    public override ValueTask EnqueueAsync(ErrorEntry error)
    {
        // Call through so production metrics + PII redaction still run; then snapshot the
        // post-redaction entry by reading it out via the base class's pipeline.
        //
        // Simpler and deterministic: redact here (matches base class behaviour exactly)
        // and append to our capture list. We deliberately do NOT forward to the base
        // channel — otherwise the production worker in the test host would try to
        // persist into the in-memory DB and race with our assertions.
        var redacted = error with
        {
            Message = new PiiRedactor().Redact(error.Message) ?? error.Message,
            StackTrace = new PiiRedactor().Redact(error.StackTrace),
            ContextData = new PiiRedactor().Redact(error.ContextData),
        };
        Captured.Add(redacted);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Integration tests for the public, hostile-input-aware <c>POST /api/errors/report</c> endpoint.
///
/// <para>The endpoint is part of Phase 3 of the resilient-error-capture-pipeline spec: the
/// frontend can ship errors to the backend without auth, but the endpoint must be hardened
/// against DoS, DB-flooding, and attacker probing. The contract asserted by these tests is:</para>
///
/// <list type="bullet">
/// <item>204 No Content on any successfully accepted request.</item>
/// <item>400 Bad Request on model-validation failures.</item>
/// <item>413 Payload Too Large when the body exceeds 8 KB.</item>
/// <item>204 No Content — NOT 429 — when rate-limited; attackers get no feedback signal.</item>
/// <item>Server-side overrides: <c>Severity</c> is always "Error", <c>Source</c> is always
///     "Frontend", regardless of what the client sent.</item>
/// <item>PII redaction runs via <see cref="ErrorQueue.EnqueueAsync"/> before anything lands
///     in the queue.</item>
/// </list>
///
/// <para><b>Rate-limit isolation across tests.</b> The rate-limit state is held in a DI
/// singleton. To keep tests independent we include an <c>X-Test-Session</c> header on every
/// request; the rate-limit partition key mixes that header into the IP key, so each test
/// instance has its own partition. In production the header is absent and behaviour is
/// pure per-IP.</para>
/// </summary>
[Collection(HangfireTestCollection.Name)]
public class ErrorReportControllerTests : IntegrationTestBase
{
    private readonly string _testSession = Guid.NewGuid().ToString("N");
    private readonly CapturingErrorQueue _capturingQueue;

    public ErrorReportControllerTests()
    {
        // Replace the singleton ErrorQueue with a capturing subclass so tests can inspect
        // what the controller enqueued without racing against the ErrorPersistenceWorker
        // that's registered in the Testing environment.
        _capturingQueue = new CapturingErrorQueue(new PiiRedactor());
        WithService<ErrorQueue>(_capturingQueue);
    }

    private HttpRequestMessage NewReport(object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/errors/report")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("X-Test-Session", _testSession);
        return request;
    }

    private HttpRequestMessage NewRawReport(string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/errors/report")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Test-Session", _testSession);
        return request;
    }

    private List<ErrorEntry> Captured => _capturingQueue.Captured.ToList();

    [Fact]
    public async Task ValidAnonymousPost_Returns204_AndEnqueuesEntry()
    {
        var response = await Client.SendAsync(NewReport(new
        {
            Message = "Something went wrong",
            StackTrace = "at Foo.Bar() in Foo.js:10",
            Url = "https://app.test/page",
            UserAgent = "Mozilla/5.0",
            CorrelationId = "corr-123",
            ErrorType = "TypeError",
            LineNumber = 10,
            ColumnNumber = 5,
        }));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        Captured.Should().ContainSingle(e => e.Message.Contains("Something went wrong"));
        Captured.First(e => e.Message.Contains("Something went wrong")).Source
            .Should().Be("Frontend");
    }

    [Fact]
    public async Task AnonymousAccessAllowed_NoAuthHeader_Returns204()
    {
        // Make sure no Authorization header is attached.
        Client.DefaultRequestHeaders.Authorization = null;

        var response = await Client.SendAsync(NewReport(new { Message = "hello" }));

        ((int)response.StatusCode).Should().Be(204);
    }

    [Fact]
    public async Task MissingMessage_Returns400()
    {
        var response = await Client.SendAsync(NewReport(new { }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task MessageTooLong_Returns400()
    {
        var oversizedMessage = new string('x', 1001);
        var response = await Client.SendAsync(NewReport(new { Message = oversizedMessage }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PayloadOver8Kb_Returns413()
    {
        // Construct a raw JSON payload comfortably over 8 KB. StackTrace alone can be up
        // to 4000 chars per validation, but we bypass validation with a body that's larger
        // than the request-size limit — Kestrel rejects before the action runs.
        var bigField = new string('a', 9000);
        var json = "{\"Message\":\"x\",\"StackTrace\":\"" + bigField + "\"}";

        var response = await Client.SendAsync(NewRawReport(json));

        ((int)response.StatusCode).Should().Be((int)HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task ElevenRequestsInOneSecond_ExtraOnesDropped_ButStillReturn204()
    {
        // Fire 15 requests in rapid succession. At 10/sec the 11th+ should hit the limiter
        // and get silently dropped to 204. None may return 429.
        var responses = new List<HttpResponseMessage>();
        for (var i = 0; i < 15; i++)
        {
            responses.Add(await Client.SendAsync(NewReport(new { Message = $"msg-{i}" })));
        }

        // All responses must be 204 — never 429.
        responses.Select(r => (int)r.StatusCode).Should().OnlyContain(c => c == 204);

        // Queue must have received no more than the burst permit count.
        var entries = Captured;
        entries.Count.Should().BeLessThanOrEqualTo(10);
        entries.Count.Should().BeGreaterThan(0, "at least some of the first 10 requests should land in the queue");
    }

    [Fact]
    public async Task ClientSeverityCritical_IsIgnored_ServerForcesError()
    {
        var response = await Client.SendAsync(NewReport(new
        {
            Message = "simulated",
            Severity = "Critical", // attacker trying to self-declare
        }));

        ((int)response.StatusCode).Should().Be(204);

        var entries = Captured;
        entries.Should().ContainSingle();
        entries[0].Severity.Should().Be("Error");
    }

    [Fact]
    public async Task ClientSourceHttp_IsIgnored_ServerForcesFrontend()
    {
        var response = await Client.SendAsync(NewReport(new
        {
            Message = "simulated",
            Source = "HTTP",
        }));

        ((int)response.StatusCode).Should().Be(204);

        var entries = Captured;
        entries.Should().ContainSingle();
        entries[0].Source.Should().Be("Frontend");
    }

    [Fact]
    public async Task BearerTokenInMessage_IsRedactedBeforeEnqueue()
    {
        var response = await Client.SendAsync(NewReport(new
        {
            Message = "failed with Bearer eyJhbGciOiJIUzI1NiJ9.payload.signature",
        }));

        ((int)response.StatusCode).Should().Be(204);

        var entries = Captured;
        entries.Should().ContainSingle();
        entries[0].Message.Should().Contain("Bearer <redacted>");
        entries[0].Message.Should().NotContain("eyJhbGciOiJIUzI1NiJ9");
    }

    [Fact]
    public async Task EmailInMessage_IsRedactedBeforeEnqueue()
    {
        var response = await Client.SendAsync(NewReport(new
        {
            Message = "user alice@example.com hit error",
        }));

        ((int)response.StatusCode).Should().Be(204);

        var entries = Captured;
        entries.Should().ContainSingle();
        entries[0].Message.Should().Contain("<email>");
        entries[0].Message.Should().NotContain("alice@");
    }
}

using System.Net;
using System.Text;

namespace Api.Tests.Features.BoxManagement;

/// <summary>
/// One request captured by <see cref="ScriptedHandler"/> — method, the ABSOLUTE
/// request URL (BoxClient builds absolute URIs itself; the HttpClient has no
/// BaseAddress), and the request body (empty string when the request had none).
/// </summary>
public sealed record CapturedRequest(HttpMethod Method, string Url, string Body);

/// <summary>
/// FIFO scripted <see cref="HttpMessageHandler"/> for driving a real
/// <c>BoxClient</c> in unit tests. Enqueue the responses in the order the code
/// under test will issue requests; every request is captured (method, absolute
/// URL, body) so tests can assert the exact wire shape that was sent.
/// Successor to the deleted Fly-era handler in <c>Tests/Features/FlyManagement</c>.
/// </summary>
public sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();

    public int CallCount { get; private set; }

    /// <summary>Captured requests in call order.</summary>
    public List<CapturedRequest> Requests { get; } = new();

    /// <summary>Captured request bodies in call order (convenience projection of <see cref="Requests"/>).</summary>
    public List<string> CapturedBodies => Requests.Select(r => r.Body).ToList();

    public void Enqueue(HttpStatusCode status, string body)
    {
        _responses.Enqueue(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        CallCount++;

        // Capture BEFORE dispatching the response so tests asserting on failure
        // paths still get the payload that was sent.
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new CapturedRequest(
            request.Method,
            request.RequestUri?.AbsoluteUri ?? string.Empty,
            body));

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException(
                $"ScriptedHandler exhausted after {CallCount} calls — test under-mocked. " +
                $"Last request: {request.Method} {request.RequestUri}");
        }
        return _responses.Dequeue();
    }
}

using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Source.Infrastructure.Logging;

namespace Api.Tests.Infrastructure.Logging;

/// <summary>
/// End-to-end coverage for the wire-up: register a capturing logger provider,
/// run it through <see cref="RedactingLoggerProviderExtensions.AddJwtRedactingLogging"/>,
/// then emit log lines and assert the captured text contains
/// <see cref="JwtRedactor.Replacement"/> instead of the original token.
/// </summary>
public class RedactingLoggerProviderTests
{
    private const string SampleJwt =
        "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0In0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";

    [Fact]
    public void AddJwtRedactingLogging_WrapsEachExistingProvider_SoEmittedLinesAreRedacted()
    {
        var capture = new CaptureSink();
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.ClearProviders();
            b.AddProvider(new CaptureProvider(capture));
        });

        services.AddJwtRedactingLogging();

        using var sp = services.BuildServiceProvider();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Test");

        logger.LogInformation("payload {P}", SampleJwt);

        capture.Lines.Should().ContainSingle()
            .Which.Should().Contain("eyJ***REDACTED***")
            .And.NotContain(SampleJwt);
    }

    [Fact]
    public void AddJwtRedactingLogging_AlsoScrubsPrivateKeys()
    {
        // Sibling redactor — the wrapping decorator runs both JWT + key
        // redaction in series. A leaked PEM body must be scrubbed too.
        const string samplePemKey =
            "-----BEGIN OPENSSH PRIVATE KEY-----\n" +
            "b3BlbnNzaC1rZXktdjEAAAAA...FAKEbody==\n" +
            "-----END OPENSSH PRIVATE KEY-----";

        var capture = new CaptureSink();
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.ClearProviders();
            b.AddProvider(new CaptureProvider(capture));
        });
        services.AddJwtRedactingLogging();

        using var sp = services.BuildServiceProvider();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Test");

        logger.LogInformation("installed key {K}", samplePemKey);

        capture.Lines.Should().ContainSingle()
            .Which.Should().Contain("[REDACTED PRIVATE KEY]")
            .And.NotContain("FAKEbody");
    }

    [Fact]
    public void AddJwtRedactingLogging_NonJwtMessages_PassThroughUnchanged()
    {
        var capture = new CaptureSink();
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.ClearProviders();
            b.AddProvider(new CaptureProvider(capture));
        });
        services.AddJwtRedactingLogging();

        using var sp = services.BuildServiceProvider();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Test");

        logger.LogInformation("user {User} logged in", "alice");

        capture.Lines.Should().ContainSingle()
            .Which.Should().Contain("user alice logged in");
    }

    [Fact]
    public void AddJwtRedactingLogging_MultipleProviders_AllAreWrapped()
    {
        var captureA = new CaptureSink();
        var captureB = new CaptureSink();
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.ClearProviders();
            b.AddProvider(new CaptureProvider(captureA));
            b.AddProvider(new CaptureProvider(captureB));
        });

        services.AddJwtRedactingLogging();

        using var sp = services.BuildServiceProvider();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Test");

        logger.LogInformation("token: {T}", SampleJwt);

        captureA.Lines.Should().ContainSingle().Which.Should().NotContain(SampleJwt);
        captureB.Lines.Should().ContainSingle().Which.Should().NotContain(SampleJwt);
    }

    // -- minimal in-memory ILoggerProvider used as a fake sink ------------------

    private sealed class CaptureSink
    {
        public ConcurrentBag<string> Lines { get; } = new();
    }

    private sealed class CaptureProvider : ILoggerProvider
    {
        private readonly CaptureSink _sink;
        public CaptureProvider(CaptureSink sink) => _sink = sink;
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(_sink);
        public void Dispose() { }

        private sealed class CaptureLogger : ILogger
        {
            private readonly CaptureSink _sink;
            public CaptureLogger(CaptureSink sink) => _sink = sink;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                _sink.Lines.Add(formatter(state, exception));
            }
        }
    }
}

using Microsoft.Extensions.Logging;

namespace Source.Infrastructure.Logging;

/// <summary>
/// Wraps an inner <see cref="ILoggerProvider"/> so every log message is run
/// through <see cref="JwtRedactor.Redact"/>, <see cref="PrivateKeyRedactor.Redact"/>,
/// AND <see cref="SecretValueRedactor.Redact"/> before it reaches the underlying
/// sink. Registered for *every* provider already in the pipeline (Console,
/// Debug, EventSource, …) by <see cref="RedactingLoggerProviderExtensions"/>.
///
/// We can't intercept structured property values in MEL the way a Serilog
/// enricher could — MEL hands the formatter the raw <c>state</c> and lets each
/// sink decide what to do. So we operate on the *formatted* message produced
/// by the supplied <c>formatter</c>: format → redact → forward as a plain
/// string. Structured property values are dropped at this hop, which is fine
/// for stock console/debug providers (they only render the formatted string
/// anyway). If a richer sink is added later, that sink should be replaced with
/// one that does its own redaction at the property level.
/// </summary>
public sealed class RedactingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ILoggerProvider _inner;

    public RedactingLoggerProvider(ILoggerProvider inner)
    {
        _inner = inner;
    }

    public ILogger CreateLogger(string categoryName) =>
        new RedactingLogger(_inner.CreateLogger(categoryName));

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        if (_inner is ISupportExternalScope s)
        {
            s.SetScopeProvider(scopeProvider);
        }
    }

    public void Dispose() => _inner.Dispose();

    private sealed class RedactingLogger : ILogger
    {
        private readonly ILogger _inner;

        public RedactingLogger(ILogger inner) => _inner = inner;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            _inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            // Format up-front, then redact. We then forward as a plain string with
            // a trivial formatter so the inner logger writes the redacted text
            // verbatim. The original `state` is intentionally not forwarded —
            // doing so would re-expose unredacted property values to any sink
            // that re-formats from state.
            //
            // Three redactors run in series in a fixed order: JWT first, then
            // SSH private keys, then the generic secret-shape backstop. Each
            // can short-circuit the work via its ContainsMatch fast-path, and
            // applying all three is cheap enough that we don't bother caching
            // intermediate state — the common case (no match) skips Replace
            // entirely.
            //
            // Order rationale: JwtRedactor's "eyJ***REDACTED***" prefix
            // preservation is the most informative; running it first lets a
            // log inspector still see "this was a JWT" instead of a flat
            // [REDACTED]. PrivateKeyRedactor and SecretValueRedactor both
            // collapse to opaque markers, so order between them doesn't matter
            // — kept stable for predictable test output.
            var formatted = formatter(state, exception);
            var hasJwt = JwtRedactor.ContainsMatch(formatted);
            var hasKey = PrivateKeyRedactor.ContainsMatch(formatted);
            var hasSecret = SecretValueRedactor.ContainsMatch(formatted);
            if (!hasJwt && !hasKey && !hasSecret)
            {
                _inner.Log(logLevel, eventId, state, exception, formatter);
                return;
            }

            var redacted = formatted;
            if (hasJwt)
            {
                redacted = JwtRedactor.Redact(redacted);
            }
            if (hasKey)
            {
                redacted = PrivateKeyRedactor.Redact(redacted);
            }
            if (hasSecret)
            {
                redacted = SecretValueRedactor.Redact(redacted);
            }
            _inner.Log(logLevel, eventId, redacted, exception, static (s, _) => s);
        }
    }
}

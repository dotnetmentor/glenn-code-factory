using System.Buffers;
using System.Text;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;

namespace Source.Features.SignalR.Diagnostics;

/// <summary>
/// Diagnostic-only decorator around <see cref="JsonHubProtocol"/> that snapshots
/// raw input bytes around <see cref="TryParseMessage"/> so we can see exactly
/// what the daemon put on the wire when parsing fails. Temporary — remove once
/// the bogus <c>target: "TurnCompleted"</c> frame is explained.
/// </summary>
public sealed class LoggingHubProtocol : IHubProtocol
{
    private readonly JsonHubProtocol _inner;
    private readonly ILogger<LoggingHubProtocol> _logger;

    public LoggingHubProtocol(JsonHubProtocol inner, ILogger<LoggingHubProtocol> logger)
    {
        _inner = inner;
        _logger = logger;
    }

    public string Name => _inner.Name;
    public int Version => _inner.Version;
    public TransferFormat TransferFormat => _inner.TransferFormat;

    public bool IsVersionSupported(int version) => _inner.IsVersionSupported(version);

    public void WriteMessage(HubMessage message, IBufferWriter<byte> output)
        => _inner.WriteMessage(message, output);

    public ReadOnlyMemory<byte> GetMessageBytes(HubMessage message)
        => _inner.GetMessageBytes(message);

    public bool TryParseMessage(ref ReadOnlySequence<byte> input, IInvocationBinder binder, out HubMessage? message)
    {
        // Snapshot up to 4096 bytes of the *current* input before the inner
        // parser advances it. We need this BEFORE the call so we still have
        // the bytes if the parser throws.
        var snapshotLen = (int)Math.Min(input.Length, 4096);
        var snapshot = input.Slice(0, snapshotLen).ToArray();

        try
        {
            var parsed = _inner.TryParseMessage(ref input, binder, out message);

            if (parsed && message is not null && _logger.IsEnabled(LogLevel.Debug))
            {
                var preview = SafeUtf8(snapshot, 512);
                if (message is InvocationMessage invocation)
                {
                    _logger.LogDebug(
                        "Hub frame parsed: type={MessageType} target={Target} invocationId={InvocationId} bytes={Bytes} preview={Preview}",
                        message.GetType().Name, invocation.Target, invocation.InvocationId, snapshotLen, preview);
                }
                else
                {
                    _logger.LogDebug(
                        "Hub frame parsed: type={MessageType} bytes={Bytes} preview={Preview}",
                        message.GetType().Name, snapshotLen, preview);
                }
            }

            return parsed;
        }
        catch (Exception ex)
        {
            var raw = SafeUtf8(snapshot, snapshot.Length);
            _logger.LogWarning(
                "Hub frame parse FAILED: bytes={Bytes} error={Error} raw={Raw}",
                snapshotLen, ex.Message, raw);
            throw;
        }
    }

    private static string SafeUtf8(byte[] bytes, int maxLen)
    {
        var len = Math.Min(bytes.Length, maxLen);
        var s = Encoding.UTF8.GetString(bytes, 0, len);
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            // Replace non-printable / control chars (except common whitespace)
            // with '?' so the log line stays single-line readable.
            if (c == '\t' || c == ' ' || (c >= 0x20 && c < 0x7F) || c > 0x7F && !char.IsControl(c))
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('?');
            }
        }
        return sb.ToString();
    }
}

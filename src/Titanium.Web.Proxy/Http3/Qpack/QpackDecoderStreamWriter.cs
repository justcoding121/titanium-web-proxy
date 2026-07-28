#pragma warning disable CA1416
using System;
using System.Net.Quic;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Http3.Qpack;

/// <summary>
///     Drains <see cref="QpackContext.DecoderAckChannel" /> and writes the pending Section Acknowledgment
///     and Insert Count Increment instructions to the peer's unidirectional QPACK decoder stream.
///     Runs as a background loop until the channel is completed (on connection close).
/// </summary>
internal static class QpackDecoderStreamWriter
{
    /// <summary>
    ///     Continuously drains the decoder ack channel and writes instructions to
    ///     <paramref name="stream" /> until the channel is completed or <paramref name="ct" /> is cancelled.
    /// </summary>
    internal static async Task RunAsync(QuicStream stream, QpackContext context, CancellationToken ct)
    {
        var reader = context.DecoderAckChannel.Reader;

        try
        {
            await foreach (var instruction in reader.ReadAllAsync(ct))
            {
                await stream.WriteAsync(instruction, ct);
            }
            await stream.FlushAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception) { /* stream closed — stop writing */ }
    }
}
#pragma warning restore CA1416

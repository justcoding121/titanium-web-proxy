using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Runs the queued (dedicated frame writer) <see cref="Http2Helper.SendBody" /> /
///     <see cref="Http2Helper.QueueSendTrailer" /> against an in-memory stream and returns once the writer has
///     drained, so tests can assert on the exact wire bytes.
/// </summary>
internal static class Http2QueuedSend
{
    public static async Task SendBody(Http2Settings settings, RequestResponseBase rr, Http2FrameHeader header,
        byte[] frameHeaderBuffer, int maxDataFrameSize, Http2FlowController flow, Stream output,
        CancellationToken cancellationToken = default)
    {
        using var cts = new CancellationTokenSource();
        var state = new Http2ConnectionState(1, cts);
        using var gate = new SemaphoreSlim(1, 1);
        await using (var writer = new Http2FrameWriter(output, gate))
        {
            state.ServerFrameWriter = writer;
            await Http2Helper.SendBody(state, towardServer: true, settings, rr, header, frameHeaderBuffer,
                maxDataFrameSize, flow, output, cancellationToken, gate);
        }
    }

    public static async Task SendTrailer(Http2Settings settings, Http2FrameHeader header, byte[] frameHeaderBuffer,
        int streamId, HeaderCollection trailers, bool endStream, Stream output)
    {
        using var cts = new CancellationTokenSource();
        var state = new Http2ConnectionState(1, cts);
        using var gate = new SemaphoreSlim(1, 1);
        await using (var writer = new Http2FrameWriter(output, gate))
        {
            state.ServerFrameWriter = writer;
            Http2Helper.QueueSendTrailer(state, towardServer: true, gate, settings, header, frameHeaderBuffer,
                streamId, trailers, endStream, output);
        }
    }
}

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Http2;

namespace Titanium.Web.Proxy.IntegrationTests.Helpers;

/// <summary>
///     A flow-control-aware wrapper over one <see cref="Http2RawFrame.Connection" />, usable on either side of the
///     proxy (raw client or raw origin). A background pump owns every read: SETTINGS are acknowledged, and
///     WINDOW_UPDATE / SETTINGS_INITIAL_WINDOW_SIZE are folded into the send windows, so
///     <see cref="SendDataAsync" /> never writes more than the peer allows. Every frame is also queued for the
///     test via <see cref="ReadFrameAsync" />. Receive-side credit is deliberately NOT granted automatically
///     unless the caller asks for it, so tests can starve the proxy and observe how it buffers.
/// </summary>
internal sealed class Http2FlowPeer : IDisposable
{
    private readonly Http2RawFrame.Connection connection;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly Channel<Http2RawFrame.Frame> frames = Channel.CreateUnbounded<Http2RawFrame.Frame>();
    private readonly CancellationTokenSource cts = new();
    private readonly object windowLock = new();
    private readonly Dictionary<int, long> streamSendWindow = new();
    private long connectionSendWindow = 65535;
    private int initialStreamWindow = 65535;

    public Http2FlowPeer(Http2RawFrame.Connection connection)
    {
        this.connection = connection;
        _ = Task.Run(PumpAsync);
    }

    public Http2RawFrame.Connection Connection => connection;

    private async Task PumpAsync()
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var frame = await connection.ReadFrameAsync();
                switch (frame.Type)
                {
                    case Http2FrameType.Settings when (frame.Flags & Http2FrameFlag.Ack) == 0:
                        ApplySettings(frame.Payload);
                        await WriteFrameAsync(Http2FrameType.Settings, 0, Http2FrameFlag.Ack, Array.Empty<byte>());
                        break;
                    case Http2FrameType.WindowUpdate when frame.Payload.Length == 4:
                        var increment = (long)(BinaryPrimitives.ReadUInt32BigEndian(frame.Payload) & 0x7fffffff);
                        lock (windowLock)
                        {
                            if (frame.StreamId == 0)
                                connectionSendWindow += increment;
                            else
                                streamSendWindow[frame.StreamId] =
                                    GetStreamWindowLocked(frame.StreamId) + increment;
                        }

                        break;
                }

                frames.Writer.TryWrite(frame);
            }
        }
        catch (Exception ex)
        {
            frames.Writer.TryComplete(ex);
        }
    }

    private void ApplySettings(byte[] payload)
    {
        for (var i = 0; i + 6 <= payload.Length; i += 6)
        {
            var id = (payload[i] << 8) | payload[i + 1];
            var value = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(i + 2, 4));
            if (id != (int)Http2SettingsId.InitialWindowSize) continue;
            lock (windowLock)
            {
                var delta = (long)value - initialStreamWindow;
                initialStreamWindow = (int)value;
                foreach (var key in new List<int>(streamSendWindow.Keys))
                    streamSendWindow[key] += delta;
            }
        }
    }

    private long GetStreamWindowLocked(int streamId) =>
        streamSendWindow.TryGetValue(streamId, out var window) ? window : initialStreamWindow;

    public async Task WriteFrameAsync(Http2FrameType type, int streamId, Http2FrameFlag flags, byte[] payload)
    {
        await writeGate.WaitAsync();
        try
        {
            await connection.WriteFrameAsync(type, streamId, flags, payload);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public Task SendInitialSettingsAsync() =>
        WriteFrameAsync(Http2FrameType.Settings, 0, 0, Array.Empty<byte>());

    public Task SendWindowUpdateAsync(int streamId, int increment)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)increment);
        return WriteFrameAsync(Http2FrameType.WindowUpdate, streamId, 0, payload);
    }

    public async Task SendHeadersAsync(int streamId, IEnumerable<(string, string)> pseudo,
        IEnumerable<(string, string)> headers, bool endStream)
    {
        // Encode and write under one gate acquisition so HPACK state and wire order stay aligned.
        await writeGate.WaitAsync();
        try
        {
            var block = connection.EncodeHeaders(pseudo, headers);
            var flags = Http2FrameFlag.EndHeaders | (endStream ? Http2FrameFlag.EndStream : 0);
            await connection.WriteFrameAsync(Http2FrameType.Headers, streamId, flags, block);
        }
        finally
        {
            writeGate.Release();
        }
    }

    /// <summary>
    ///     Sends <paramref name="data" /> as DATA frames of at most 16384 bytes, waiting for send-window credit
    ///     before each frame and never exceeding what the peer has granted.
    /// </summary>
    public async Task SendDataAsync(int streamId, byte[] data, bool endStream, TimeSpan timeout)
    {
        var offset = 0;
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            var want = Math.Min(16384, data.Length - offset);
            var grant = want == 0 ? 0 : await AcquireWindowAsync(streamId, want, deadline);
            var last = offset + grant >= data.Length;
            var chunk = new byte[grant];
            Buffer.BlockCopy(data, offset, chunk, 0, grant);
            await WriteFrameAsync(Http2FrameType.Data, streamId, last && endStream ? Http2FrameFlag.EndStream : 0,
                chunk);
            offset += grant;
        } while (offset < data.Length);
    }

    /// <summary>Sends one padded DATA frame (<c>pad length | data | padding</c>) spending the full cost of window.</summary>
    public async Task SendPaddedDataAsync(int streamId, byte[] data, int padLength, bool endStream, TimeSpan timeout)
    {
        var payload = new byte[1 + data.Length + padLength];
        payload[0] = (byte)padLength;
        Buffer.BlockCopy(data, 0, payload, 1, data.Length);
        // Padding bytes stay zero (RFC 9113 6.1: padding octets MUST be zero).
        var deadline = DateTime.UtcNow + timeout;
        var granted = await AcquireWindowAsync(streamId, payload.Length, deadline, requireAll: true);
        if (granted != payload.Length)
            throw new InvalidOperationException($"Expected {payload.Length} window bytes but got {granted}.");
        var flags = Http2FrameFlag.Padded | (endStream ? Http2FrameFlag.EndStream : 0);
        await WriteFrameAsync(Http2FrameType.Data, streamId, flags, payload);
    }

    private async Task<int> AcquireWindowAsync(int streamId, int want, DateTime deadline, bool requireAll = false)
    {
        while (true)
        {
            lock (windowLock)
            {
                var available = Math.Min(connectionSendWindow, GetStreamWindowLocked(streamId));
                var take = (int)Math.Min(want, available);
                if (take > 0 && (!requireAll || take == want))
                {
                    connectionSendWindow -= take;
                    streamSendWindow[streamId] = GetStreamWindowLocked(streamId) - take;
                    return take;
                }
            }

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting for send-window credit on stream {streamId}.");
            await Task.Delay(5);
        }
    }

    /// <summary>Next frame the peer sent, or null when nothing arrives within <paramref name="timeout" />.</summary>
    public async Task<Http2RawFrame.Frame?> ReadFrameAsync(TimeSpan timeout)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        try
        {
            return await frames.Reader.ReadAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public readonly record struct Message(
        int StreamId, List<(string Name, string Value)> Headers, byte[] Body, bool EndedOnHeaders);

    /// <summary>
    ///     Reads one HEADERS(+CONTINUATION) block and the DATA that follows through END_STREAM, stripping padding.
    ///     When <paramref name="autoCredit" /> is set it returns receive credit for every DATA frame.
    ///     RST_STREAM and GOAWAY fail the read.
    /// </summary>
    public async Task<Message> ReadMessageAsync(bool autoCredit, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var body = new MemoryStream();
        List<(string Name, string Value)>? headers = null;
        var streamId = -1;
        var block = new MemoryStream();
        var inBlock = false;
        var blockEndStream = false;

        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("Timed out waiting for a complete message.");
            var next = await ReadFrameAsync(remaining);
            if (next == null) throw new TimeoutException("Timed out waiting for a complete message.");
            var frame = next.Value;

            switch (frame.Type)
            {
                case Http2FrameType.Headers:
                    streamId = frame.StreamId;
                    block.SetLength(0);
                    block.Write(frame.Payload, 0, frame.Payload.Length);
                    blockEndStream = (frame.Flags & Http2FrameFlag.EndStream) != 0;
                    inBlock = (frame.Flags & Http2FrameFlag.EndHeaders) == 0;
                    break;
                case Http2FrameType.Continuation when inBlock:
                    block.Write(frame.Payload, 0, frame.Payload.Length);
                    inBlock = (frame.Flags & Http2FrameFlag.EndHeaders) == 0;
                    break;
                case Http2FrameType.Data when frame.StreamId == streamId:
                    var (offset, length) = UnpadRange(frame);
                    body.Write(frame.Payload, offset, length);
                    if (autoCredit && frame.Payload.Length > 0)
                    {
                        await SendWindowUpdateAsync(0, frame.Payload.Length);
                        if ((frame.Flags & Http2FrameFlag.EndStream) == 0)
                            await SendWindowUpdateAsync(streamId, frame.Payload.Length);
                    }

                    if ((frame.Flags & Http2FrameFlag.EndStream) != 0)
                        return new Message(streamId, headers ?? new(), body.ToArray(), false);
                    break;
                case Http2FrameType.RstStream:
                    throw new IOException(
                        $"Stream {frame.StreamId} reset: {(Http2ErrorCode)BinaryPrimitives.ReadUInt32BigEndian(frame.Payload)}.");
                case Http2FrameType.GoAway:
                    throw new IOException(
                        $"GOAWAY: {(Http2ErrorCode)BinaryPrimitives.ReadUInt32BigEndian(frame.Payload.AsSpan(4, 4))}.");
            }

            if (!inBlock && block.Length > 0 && headers == null)
            {
                headers = connection.DecodeHeaders(block.ToArray());
                block.SetLength(0);
                if (blockEndStream) return new Message(streamId, headers, Array.Empty<byte>(), true);
            }
        }
    }

    public static (int Offset, int Length) UnpadRange(Http2RawFrame.Frame frame)
    {
        if ((frame.Flags & Http2FrameFlag.Padded) == 0)
            return (0, frame.Payload.Length);
        var pad = frame.Payload[0];
        return (1, frame.Payload.Length - 1 - pad);
    }

    public void Dispose()
    {
        cts.Cancel();
    }
}

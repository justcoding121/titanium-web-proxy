using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Http2;

/// <summary>
///     A fake "origin" stream used to drive <see cref="Http2Helper.SendHttp2" /> for the h2-client-to-HTTP/1.1
///     translation bridge, which never actually forwards h2 frames to a real h2 origin - every stream is instead
///     answered independently (see <see cref="Http2StreamContext" />) by its own HTTP/1.1 origin round trip. This
///     still needs to look enough like a real (silent, well-behaved) h2 server to <see cref="Http2Helper" />'s
///     generic relay loop that nothing on the client=>server leg ever appears to fail or hang unexpectedly:
///     <list type="bullet">
///         <item>
///             Exactly one connection SETTINGS frame is produced on the first read (ENABLE_PUSH=0 and
///             INITIAL_WINDOW_SIZE = 768 KiB), because the client=>server relay direction
///             requires one before it will emit any client-facing HEADERS (see
///             <see cref="Http2ConnectionState.ServerSettingsRelayed" />) - including the bridge's own synthetic
///             responses, which reuse that exact signal. The large INITIAL_WINDOW_SIZE is relayed to the
///             client so concurrent 64 KiB uploads are not capped at the RFC default 65535.
///         </item>
///         <item>
///             Every write (the client's re-encoded HEADERS/DATA that <see cref="Http2Helper" /> forwards toward
///             "the server" once a request is not answered synthetically at BeforeRequest time, and any
///             same-leg control-frame replies) is silently discarded - the bridge answers every request itself,
///             so nothing forwarded here is ever meant to reach a real peer.
///         </item>
///         <item>
///             Every read after the initial SETTINGS frame blocks until <paramref name="cancellationToken"/> (from
///             the constructor) is cancelled, which happens once the real client-facing leg ends for any reason
///             (see <see cref="Http2Helper.SendHttp2" />) - it must never return 0 (EOF) on its own, since that
///             would make the generic relay loop treat this direction as "the server closed the connection" and
///             tear down every other multiplexed stream along with it.
///         </item>
///     </list>
/// </summary>
internal sealed class NullOriginStream : Stream
{
    /// <summary>
    ///     SETTINGS frame: ENABLE_PUSH=0 (6 bytes) + INITIAL_WINDOW_SIZE=768 KiB (6 bytes).
    ///     Length = 12, type = SETTINGS, flags = 0, stream id = 0.
    /// </summary>
    private static readonly byte[] ClientFacingSettingsFrame = BuildClientFacingSettingsFrame();

    private readonly CancellationToken cancellationToken;
    private int settingsBytesServed;

    private static byte[] BuildClientFacingSettingsFrame()
    {
        // 9-byte header + 12-byte payload (two SETTINGS entries).
        var frame = new byte[9 + 12];
        // Length = 12
        frame[0] = 0;
        frame[1] = 0;
        frame[2] = 12;
        frame[3] = (byte)Http2FrameType.Settings;
        frame[4] = 0; // flags
        // stream id = 0 (bytes 5-8 already zero)

        var payload = frame.AsSpan(9);
        // SETTINGS_ENABLE_PUSH = 0
        BinaryPrimitives.WriteUInt16BigEndian(payload, (ushort)Http2SettingsId.EnablePush);
        BinaryPrimitives.WriteUInt32BigEndian(payload.Slice(2), 0);
        // SETTINGS_INITIAL_WINDOW_SIZE = 768 KiB
        BinaryPrimitives.WriteUInt16BigEndian(payload.Slice(6), (ushort)Http2SettingsId.InitialWindowSize);
        BinaryPrimitives.WriteUInt32BigEndian(payload.Slice(8), (uint)Http2Helper.ClientInitialStreamWindowSize);
        return frame;
    }

    internal NullOriginStream(CancellationToken cancellationToken)
    {
        this.cancellationToken = cancellationToken;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException("Use ReadAsync.");
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
        CancellationToken cancellationToken)
    {
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (settingsBytesServed < ClientFacingSettingsFrame.Length)
        {
            var remaining = ClientFacingSettingsFrame.Length - settingsBytesServed;
            var toCopy = Math.Min(buffer.Length, remaining);
            ClientFacingSettingsFrame.AsMemory(settingsBytesServed, toCopy).CopyTo(buffer);
            settingsBytesServed += toCopy;
            return toCopy;
        }

        // No real server will ever send anything else on this connection - every request is answered
        // independently by the bridge. Block until the connection is torn down rather than returning 0
        // (EOF), which the generic relay loop would otherwise treat as an unexpected server disconnect and
        // use as a reason to tear down every other multiplexed stream on the client-facing leg too.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(this.cancellationToken, cancellationToken);
        await Task.Delay(Timeout.Infinite, linked.Token);
        return 0; // unreachable: Task.Delay(Timeout.Infinite, ...) only ever completes by throwing.
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        // discarded - see class remarks.
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        // discarded - see class remarks.
        return ValueTask.CompletedTask;
    }
}

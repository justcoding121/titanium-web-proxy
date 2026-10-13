using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Options;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class WebSocketElidedDelayTests
{
    [TestMethod]
    public async Task ObserveOversize_DelayZero_RelaysWithoutWaiting()
    {
        var elapsed = await RelayOversizeAsync(TimeSpan.Zero);
        Assert.IsTrue(elapsed < TimeSpan.FromMilliseconds(150), $"elapsed {elapsed}");
    }

    [TestMethod]
    public async Task ObserveOversize_HandlerDelay_IsAppliedOnceBeforeTheRawWrite()
    {
        var elapsed = await RelayOversizeAsync(TimeSpan.FromMilliseconds(200));
        Assert.IsTrue(elapsed >= TimeSpan.FromMilliseconds(150), $"elapsed {elapsed}");
        Assert.IsTrue(elapsed < TimeSpan.FromSeconds(2), $"elapsed {elapsed}");
    }

    private static async Task<TimeSpan> RelayOversizeAsync(TimeSpan delay)
    {
        var payload = new byte[20];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)i;
        }

        var raw = BuildFrame(payload);
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        proxy.PolicyModes = proxy.PolicyModes.With(PolicyFamily.WebSocketFrameBudget, PolicyMode.Observe);
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        using var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        using var session = new SessionEventArgs(proxy, endPoint, clientStream, null, cts);
        session.MaxWebSocketFramePayloadBytes = 8;
        session.WebSocketFrameElided += (_, args) =>
        {
            Assert.AreEqual(TimeSpan.Zero, args.Delay);
            Assert.AreEqual(20, args.DeclaredPayloadLength);
            args.Delay = delay;
            return Task.CompletedTask;
        };

        await using var client = new DuplexBuffer(raw, holdOpen: false);
        await using var server = new DuplexBuffer([], holdOpen: true);
        var watch = Stopwatch.StartNew();
        await WebSocketInterceptRelay.RelayAsync(client, server, proxy.BufferPool, session, cts);
        watch.Stop();
        CollectionAssert.AreEqual(raw, server.Written);
        return watch.Elapsed;
    }

    private static byte[] BuildFrame(byte[] payload)
    {
        var bytes = new byte[2 + payload.Length];
        bytes[0] = 0x82;
        bytes[1] = (byte)payload.Length;
        payload.CopyTo(bytes, 2);
        return bytes;
    }

    private sealed class DuplexBuffer : Stream
    {
        private readonly byte[] _inbound;
        private readonly bool _holdOpen;
        private readonly MemoryStream _outbound = new();
        private int _pos;

        public DuplexBuffer(byte[] inbound, bool holdOpen)
        {
            _inbound = inbound;
            _holdOpen = holdOpen;
        }

        public byte[] Written => _outbound.ToArray();

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

        public override int Read(byte[] buffer, int offset, int count) =>
            CopyInbound(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = CopyInbound(buffer.Span);
            if (n > 0 || !_holdOpen)
            {
                return new ValueTask<int>(n);
            }

            return new ValueTask<int>(HoldAsync(cancellationToken));
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            _outbound.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _outbound.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        private int CopyInbound(Span<byte> buffer)
        {
            var n = Math.Min(buffer.Length, _inbound.Length - _pos);
            if (n <= 0)
            {
                return 0;
            }

            _inbound.AsSpan(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }

        private static async Task<int> HoldAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }
    }
}

#pragma warning disable TWP001
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Http3;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Options;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.UnitTests;

internal static class Wave42FatGapTestSupport
{
    internal static readonly BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    internal static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static MethodInfo BridgeMethod(string name) =>
        typeof(Http3OriginBridge).GetMethod(name, PrivateStatic)
        ?? throw new InvalidOperationException($"HTTP/3 bridge method {name} was not found.");

    internal static MethodInfo ProxyMethod(string name) =>
        typeof(ProxyServer).GetMethod(name, PrivateStatic)
        ?? throw new InvalidOperationException($"ProxyServer method {name} was not found.");

    internal static (Http2FrameHeader Header, byte[] Buffer) FrameScratch(int streamId = 1) =>
        (new Http2FrameHeader { StreamId = streamId }, new byte[9]);

    internal static SessionEventArgs MakeSession(ProxyServer proxy, ProxyEndPoint? endPoint = null)
    {
        endPoint ??= new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        return new SessionEventArgs(proxy, endPoint, clientStream, null, cts);
    }

    internal static async Task<Http2OriginConnection> CreateShellAsync(ProxyServer proxy)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accept = listener.AcceptSocketAsync();
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var accepted = await accept;
        accepted.Dispose();

        var stream = new HttpServerStream(proxy, new NetworkStream(client, ownsSocket: true),
            new DefaultBufferPool(), CancellationToken.None);
        var serverConn = new TcpServerConnection(proxy, client, stream, "origin.test", 443, true,
            default, HttpHeader.Version20, null, null, "h2-origin");

        var ctor = typeof(Http2OriginConnection).GetConstructor(PrivateInstance, null,
            [typeof(TcpServerConnection), typeof(Microsoft.Extensions.Logging.ILogger),
                typeof(ProxyResourceLimits), typeof(int)], null)!;
        return (Http2OriginConnection)ctor.Invoke([serverConn, NullLogger.Instance,
            ProxyResourceLimits.Default, 64 * 1024])!;
    }

    internal delegate bool TryReadPrefixedIntDelegate(ReadOnlySpan<byte> data, int prefixBits, out ulong value,
        out int consumed);
    internal delegate bool TryReadStringLiteralDelegate(ReadOnlySpan<byte> data, out string result, out int consumed);
    internal delegate string HuffmanDecodeDelegate(ReadOnlySpan<byte> data);
    internal delegate int WriteHpackPrefixedIntDelegate(Span<byte> dest, byte patternByte, int prefixBits, ulong value);
    internal delegate int WriteHpackAsciiLiteralBytesDelegate(Span<byte> dest, ReadOnlySpan<byte> value);
    internal delegate int WriteHpackAsciiLiteralStringDelegate(Span<byte> dest, string value);
    internal delegate ReadOnlyMemory<byte> GetMemoryStreamMemoryDelegate(MemoryStream ms);

    internal sealed class DeferredFirstWriteStream : Stream
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writes;
        public MemoryStream Written { get; } = new();
        public void Unblock() => _gate.TrySetResult();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Written.Length;
        public override long Position { get => Written.Position; set => Written.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _writes) == 1)
                return new ValueTask(WriteAfterGateAsync(buffer));
            Written.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        private async Task WriteAfterGateAsync(ReadOnlyMemory<byte> buffer)
        {
            await _gate.Task;
            Written.Write(buffer.Span);
        }
    }
}
#pragma warning restore TWP001

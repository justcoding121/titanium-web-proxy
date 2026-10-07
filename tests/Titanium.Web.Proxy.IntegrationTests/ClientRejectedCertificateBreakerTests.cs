using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Regression-2026-10-07: a client that rejects the proxy certificate (pinning / untrusted root) used to
///     retry the MITM handshake hundreds of times, each logging a stack. After N consecutive aborts the host
///     must be learned as bypass so the client's next CONNECT is tunnelled opaque.
/// </summary>
[TestClass]
[DoNotParallelize]
public class ClientRejectedCertificateBreakerTests
{
    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    [Timeout(60 * 1000)]
    public async Task ClientRejectingProxyCertificate_Repeatedly_LearnsBypass_AndStopsHandshakeStorm()
    {
        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.EnableDecryptFailureBypass = true;
        proxy.ClientHandshakeRejectThreshold = 3;

        var learned = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.DecryptFailureBypassChanged += (_, e) => learned.TrySetResult(e.Host);

        var port = proxy.ProxyEndPoints[0].Port;
        const string host = "pinned.example.test";

        for (var i = 0; i < 3; i++)
        {
            Assert.IsFalse(proxy.ShouldBypassDecryptForLearnedHost(host), $"bypass before abort #{i + 1}");
            await AbortDuringHandshakeAsync(proxy, port, host);
        }

        var winner = await Task.WhenAny(learned.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.AreSame(learned.Task, winner, "bypass should be learned after the third rejected handshake");
        Assert.AreEqual(host, await learned.Task);
        Assert.IsTrue(proxy.ShouldBypassDecryptForLearnedHost(host));
    }

    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    [Timeout(60 * 1000)]
    public async Task AcceptedHandshakeBetweenRejections_DoesNotLearnBypass()
    {
        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        proxy.EnableDecryptFailureBypass = true;
        proxy.ClientHandshakeRejectThreshold = 3;
        var port = proxy.ProxyEndPoints[0].Port;
        const string host = "flaky.example.test";

        await AbortDuringHandshakeAsync(proxy, port, host);
        await AbortDuringHandshakeAsync(proxy, port, host);
        await AcceptingHandshakeAsync(proxy, port, host);
        await AbortDuringHandshakeAsync(proxy, port, host);
        await AbortDuringHandshakeAsync(proxy, port, host);

        Assert.IsFalse(proxy.ShouldBypassDecryptForLearnedHost(host),
            "a completed handshake proves the client trusts the proxy root; the count must reset");
    }

    private static async Task<SslStream> ConnectTunnelAsync(int proxyPort, string host, TcpClient client)
    {
        await client.ConnectAsync(IPAddress.Loopback, proxyPort);
        var stream = client.GetStream();
        var connect = Encoding.ASCII.GetBytes($"CONNECT {host}:443 HTTP/1.1\r\nHost: {host}:443\r\n\r\n");
        await stream.WriteAsync(connect);

        var buffer = new byte[1024];
        var read = await stream.ReadAsync(buffer);
        var text = Encoding.ASCII.GetString(buffer, 0, read);
        Assert.IsTrue(text.Contains("200", StringComparison.Ordinal), text);
        return new SslStream(stream, false, (_, _, _, _) => true);
    }

    /// <summary>
    ///     CONNECT, then drop the socket before any TLS record. This is the failure captured for pinned
    ///     clients: AuthenticateAsServerAsync throws "unexpected EOF", not a clean close.
    /// </summary>
    private static async Task AbortDuringHandshakeAsync(ProxyServer proxy, int proxyPort, string host)
    {
        var strikesBefore = proxy.ClientHandshakeRejects.Strikes(host);
        var bypassBefore = proxy.ShouldBypassDecryptForLearnedHost(host);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxyPort);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {host}:443 HTTP/1.1\r\nHost: {host}:443\r\n\r\n"));
        var buffer = new byte[1024];
        _ = await stream.ReadAsync(buffer);

        // Send a real ClientHello, then kill the socket before Finished so the server is still inside
        // AuthenticateAsServerAsync (the pinned-client failure: unexpected EOF, not a clean close).
        using var aborting = new CloseAfterFirstWriteStream(stream, client.Client);
        using var ssl = new SslStream(aborting, false, (_, _, _, _) => true);
        try
        {
            await ssl.AuthenticateAsClientAsync(host).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is System.IO.IOException
                                       or System.Security.Authentication.AuthenticationException
                                       or System.ObjectDisposedException
                                       or System.OperationCanceledException
                                       or TimeoutException)
        {
        }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (proxy.ShouldBypassDecryptForLearnedHost(host) != bypassBefore
                || proxy.ClientHandshakeRejects.Strikes(host) != strikesBefore)
                return;
            await Task.Delay(20);
        }

        Assert.Fail("proxy did not observe the aborted handshake");
    }

    private static async Task AcceptingHandshakeAsync(ProxyServer proxy, int proxyPort, string host)
    {
        using var client = new TcpClient();
        var ssl = await ConnectTunnelAsync(proxyPort, host, client);
        await ssl.AuthenticateAsClientAsync(host).WaitAsync(TimeSpan.FromSeconds(10));
        // Clean close without sending a request: the proxy's first decrypted read sees EOF, no TLS alert.
        await ssl.ShutdownAsync();
        await ssl.DisposeAsync();
        client.Close();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (proxy.ClientHandshakeRejects.Strikes(host) != 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.AreEqual(0, proxy.ClientHandshakeRejects.Strikes(host), "a completed handshake must clear the strikes");
    }

    /// <summary>Forwards one write (the ClientHello), then aborts the socket.</summary>
    private sealed class CloseAfterFirstWriteStream : Stream
    {
        private readonly Stream inner;
        private readonly Socket socket;
        private int writes;

        public CloseAfterFirstWriteStream(Stream inner, Socket socket)
        {
            this.inner = inner;
            this.socket = socket;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => 0;

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            if (Interlocked.Increment(ref writes) == 1)
                socket.Close();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            return 0;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            if (Interlocked.Increment(ref writes) == 1)
                socket.Close();
        }
    }
}

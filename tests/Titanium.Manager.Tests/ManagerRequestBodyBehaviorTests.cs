using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Manager.Api;

namespace Titanium.Manager.Tests;

/// <summary>
///     Bounded body reads for the manager HTTP surface.
///     Bug classes: oversize Content-Length accepted, empty JSON body fails, chunked overflow ignored.
/// </summary>
[TestClass]
public class ManagerRequestBodyBehaviorTests
{
    [TestMethod]
    public async Task ReadJsonAsync_EmptyBody_BecomesEmptyObject()
    {
        await using var host = await BodyHost.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(host.Prefix) };
        using var response = await client.PostAsync("/json", new StringContent(""));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("{}", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task ReadBytesAsync_DeclaredLengthOverCap_Returns413()
    {
        await using var host = await BodyHost.StartAsync();
        // HttpClient refuses mismatched Content-Length; speak HTTP on the wire directly.
        var uri = new Uri(host.Prefix);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(uri.Host, uri.Port);
        await using var stream = tcp.GetStream();
        var request = Encoding.ASCII.GetBytes(
            $"POST /bytes HTTP/1.1\r\nHost: {uri.Host}\r\nContent-Length: {ManagerRequestBody.MaxBodyBytes + 1}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request);
        using var reader = new StreamReader(stream, Encoding.ASCII);
        var statusLine = await reader.ReadLineAsync();
        Assert.IsNotNull(statusLine);
        StringAssert.Contains(statusLine, "413");
    }

    [TestMethod]
    public async Task ReadBytesAsync_StreamingPastCap_Returns413()
    {
        await using var host = await BodyHost.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(host.Prefix) };
        // Chunked transfer: ContentLength stays unknown (-1), so the mid-read cap applies.
        using var content = new StreamContent(new OversizedStream(ManagerRequestBody.MaxBodyBytes + 4096));
        using var response = await client.PostAsync("/bytes", content);
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    private sealed class OversizedStream(int length) : Stream
    {
        private int _remaining = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0)
            {
                return 0;
            }

            var n = Math.Min(count, Math.Min(_remaining, buffer.Length - offset));
            Array.Clear(buffer, offset, n);
            _remaining -= n;
            return n;
        }
    }

    private sealed class BodyHost : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private Task? _loop;

        public string Prefix { get; private set; } = "";

        public static async Task<BodyHost> StartAsync()
        {
            var host = new BodyHost();
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var port = FreePort();
                host.Prefix = $"http://127.0.0.1:{port}/";
                host._listener.Prefixes.Clear();
                host._listener.Prefixes.Add(host.Prefix);
                try
                {
                    host._listener.Start();
                    host._loop = host.RunAsync();
                    return host;
                }
                catch (Exception ex) when (ex is HttpListenerException or SocketException)
                {
                    // retry with another ephemeral port
                }
            }

            throw new InvalidOperationException("could not bind BodyHost");
        }

        private async Task RunAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().WaitAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException or OperationCanceledException)
                {
                    return;
                }

                try
                {
                    if (context.Request.Url?.AbsolutePath == "/json")
                    {
                        using var doc = await ManagerRequestBody.ReadJsonAsync(context).ConfigureAwait(false);
                        var bytes = Encoding.UTF8.GetBytes(doc.RootElement.GetRawText());
                        context.Response.StatusCode = 200;
                        context.Response.ContentType = "application/json";
                        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                    }
                    else
                    {
                        try
                        {
                            var body = await ManagerRequestBody.ReadBytesAsync(context).ConfigureAwait(false);
                            context.Response.StatusCode = 200;
                            await context.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
                        }
                        catch (RequestTooLargeException)
                        {
                            context.Response.StatusCode = 413;
                        }
                    }
                }
                finally
                {
                    try { context.Response.Close(); } catch { /* ignore */ }
                }
            }
        }

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            try { _listener.Stop(); } catch { /* ignore */ }
            try { _listener.Close(); } catch { /* ignore */ }
            if (_loop is not null)
            {
                try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { /* ignore */ }
            }

            _cts.Dispose();
        }
    }
}

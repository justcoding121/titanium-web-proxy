using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests.Helpers;

/// <summary>What a <see cref="ScriptedTlsOrigin" /> does with one connection or one request.</summary>
public enum OriginAction
{
    /// <summary>Answer <c>200 ok</c> with keep-alive.</summary>
    Respond,

    /// <summary>Read the request head and close without sending a byte (stale / dying origin).</summary>
    CloseWithoutResponse,

    /// <summary>Read the request head and never answer (response-header timeout).</summary>
    NeverRespond,

    /// <summary>Send a 200 head promising 100 body bytes, a few of them, then close (headers already committed).</summary>
    PartialBodyThenClose,

    /// <summary>Connection level: reset the TCP connection before the TLS handshake (RST).</summary>
    ResetBeforeTls
}

/// <summary>
///     Scriptable TLS HTTP/1.1 origin for failure-path tests. <paramref name="decide" /> receives the 1-based
///     connection index and the 0-based request index on that connection and returns the action for that request;
///     request index <c>-1</c> is asked once per connection before the TLS handshake (only
///     <see cref="OriginAction.ResetBeforeTls" /> is honoured there, anything else proceeds).
/// </summary>
public sealed class ScriptedTlsOrigin : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource cts = new();
    private readonly Func<int, int, OriginAction> decide;
    private int accepted;
    private int requests;

    public ScriptedTlsOrigin(Func<int, int, OriginAction> decide)
    {
        this.decide = decide;
        listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    /// <summary>TCP connections accepted so far.</summary>
    public int Accepted => Volatile.Read(ref accepted);

    /// <summary>Request heads read so far.</summary>
    public int Requests => Volatile.Read(ref requests);

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var tcp = await listener.AcceptTcpClientAsync(cts.Token);
                var index = Interlocked.Increment(ref accepted);
                _ = Task.Run(() => ServeAsync(tcp, index));
            }
        }
        catch (Exception)
        {
            // listener stopped
        }
    }

    private async Task ServeAsync(TcpClient tcp, int index)
    {
        try
        {
            using (tcp)
            {
                if (decide(index, -1) == OriginAction.ResetBeforeTls)
                {
                    tcp.LingerState = new LingerOption(true, 0);
                    return;
                }

                await using var ssl = new SslStream(tcp.GetStream(), false);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = TestCertificateAuthority.ServerCertificate,
                    ApplicationProtocols = new() { SslApplicationProtocol.Http11 },
                }, cts.Token);

                for (var served = 0; !cts.IsCancellationRequested; served++)
                {
                    if (!await ReadRequestHeadAsync(ssl))
                        return;

                    Interlocked.Increment(ref requests);
                    switch (decide(index, served))
                    {
                        case OriginAction.CloseWithoutResponse:
                            return;
                        case OriginAction.NeverRespond:
                            await Task.Delay(Timeout.Infinite, cts.Token);
                            return;
                        case OriginAction.PartialBodyThenClose:
                            await ssl.WriteAsync(Encoding.ASCII.GetBytes(
                                "HTTP/1.1 200 OK\r\nContent-Length: 100\r\nContent-Type: text/plain\r\n\r\npartial"),
                                cts.Token);
                            await ssl.FlushAsync(cts.Token);
                            return;
                        default:
                            await ssl.WriteAsync(Encoding.ASCII.GetBytes(
                                "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nContent-Type: text/plain\r\n\r\nok"),
                                cts.Token);
                            await ssl.FlushAsync(cts.Token);
                            break;
                    }
                }
            }
        }
        catch (Exception)
        {
            // client side closed
        }
    }

    private async Task<bool> ReadRequestHeadAsync(Stream stream)
    {
        var window = 0u;
        var buf = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(buf, cts.Token) == 0)
                return false;
            window = (window << 8) | buf[0];
            if (window == 0x0D0A0D0A)
                return true;
        }
    }

    public void Dispose()
    {
        cts.Cancel();
        listener.Stop();
        cts.Dispose();
    }
}

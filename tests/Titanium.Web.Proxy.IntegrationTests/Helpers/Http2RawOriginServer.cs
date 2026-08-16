using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Http2.Hpack;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests.Helpers;

/// <summary>
///     A minimal, hand-rolled HTTP/2 origin server used to exercise proxy behavior that a real HTTP/2
///     server (Kestrel) either cannot easily be told to do (send an interim 1xx, split a header block
///     across CONTINUATION frames, send trailers with exact byte control) or a real HTTP/2 client
///     (SocketsHttpHandler) has no public API for on the request side (see <see cref="Http2RawFrame" />
///     for the shared frame read/write helpers).
///     <para>
///         Speaks real TLS with ALPN "h2", using a certificate issued by the same test root CA the proxy
///         under test is configured to trust for upstream connections (see
///         <see cref="TestCertificateAuthority" />), so it can be used as a normal HTTPS upstream target
///         (<see cref="Url" />) exactly as a real origin server would be - the proxy itself cannot tell the
///         difference.
///     </para>
/// </summary>
internal sealed class Http2RawOriginServer : IDisposable
{
    private readonly TcpListener listener;
    private readonly X509Certificate2? certificate;
    private readonly bool cleartext;
    private Func<Http2RawFrame.Connection, Task> handler = null!;
    private bool disposed;

    public Http2RawOriginServer(X509Certificate2 certificate)
        : this(certificate, cleartext: false)
    {
    }

    /// <summary>
    ///     Cleartext HTTP/2 prior-knowledge (h2c) origin — no TLS.
    /// </summary>
    public static Http2RawOriginServer CreateCleartext() => new(null, cleartext: true);

    private Http2RawOriginServer(X509Certificate2? certificate, bool cleartext)
    {
        this.certificate = certificate;
        this.cleartext = cleartext;
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public string Url => cleartext ? $"http://127.0.0.1:{Port}/" : $"https://localhost:{Port}/";

    /// <summary>
    ///     The number of raw TCP connections this server has accepted so far, counted at the moment of
    ///     accept - before the TLS/ALPN handshake and connection preface. This includes connections that
    ///     never make it past the TLS handshake (e.g. the proxy's own "does the origin support HTTP/2"
    ///     probe, which opens a connection purely to read the negotiated ALPN and then closes it without
    ///     ever sending the HTTP/2 connection preface), so it can be used to assert on the total number of
    ///     TLS handshakes a test scenario caused, independent of how many of those turned into real requests.
    /// </summary>
    public int AcceptedConnectionCount => acceptedConnectionCount;

    private int acceptedConnectionCount;

    /// <summary>
    ///     Sets the handler invoked for each accepted connection, after the TLS/ALPN handshake (when
    ///     applicable) and the client connection preface have already been consumed.
    /// </summary>
    public void HandleConnection(Func<Http2RawFrame.Connection, Task> connectionHandler)
    {
        handler = connectionHandler;
    }

    private async Task AcceptLoopAsync()
    {
        while (!disposed)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync();
            }
            catch
            {
                return;
            }

            Interlocked.Increment(ref acceptedConnectionCount);

            _ = Task.Run(async () =>
            {
                try
                {
                    Stream stream;
                    if (cleartext)
                    {
                        stream = client.GetStream();
                    }
                    else
                    {
                        var sslStream = new SslStream(client.GetStream(), false);
                        await sslStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                        {
                            ServerCertificate = certificate!,
                            ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http2 },
                            EnabledSslProtocols = System.Security.Authentication.SslProtocols.None
                        });
                        stream = sslStream;
                    }

                    var preface = new byte[Http2Helper.ConnectionPreface.Length];
                    await Http2RawFrame.ReadExactAsync(stream, preface, 0, preface.Length);

                    var connection = new Http2RawFrame.Connection(stream);
                    var currentHandler = handler;
                    if (currentHandler != null)
                    {
                        await currentHandler(connection);
                    }

                    // The proxy may still be sending WINDOW_UPDATE/PING/etc. frames in reaction to
                    // what we just wrote (e.g. credit grants for DATA frames the handler sent). A real
                    // HTTP/2 server keeps reading from the socket for the lifetime of the connection; this
                    // minimal test harness does not, so closing the socket immediately can leave those
                    // bytes sitting unread in the OS receive buffer. Closing a socket with unread inbound
                    // data queued triggers an abortive RST instead of a graceful FIN, and that RST can
                    // cause the peer's OS to discard bytes it already received but had not yet handed to
                    // user space - non-deterministically dropping frames (e.g. trailers) that were fully
                    // sent over the wire. Drain any such pending bytes with a short bounded timeout before
                    // disposing so the close is graceful.
                    await DrainAsync(stream);
                }
                catch (Exception ex)
                {
                    // swallow - test assertions on the client/proxy side will surface the failure, but log
                    // for diagnostics since an exception here otherwise fails silently.
                    System.Diagnostics.Debug.WriteLine("Http2RawOriginServer connection handler failed: " + ex);
                }
                finally
                {
                    client.Dispose();
                }
            });
        }
    }

    /// <summary>
    ///     Reads and discards any bytes that arrive on <paramref name="stream" /> within a short grace
    ///     period, so that closing the socket right afterwards does not abort a connection that still has
    ///     unread inbound data queued (which the OS turns into an RST instead of a graceful FIN, and can
    ///     make the peer lose bytes it already received but had not yet consumed).
    /// </summary>
    private static async Task DrainAsync(Stream stream)
    {
        var buffer = new byte[4096];
        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(buffer, cts.Token);
                if (read <= 0)
                {
                    // graceful EOF from the peer - nothing more to drain.
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // grace period elapsed with no more data (or a read was in-flight) - proceed to close.
        }
        catch (Exception)
        {
            // any other read failure means the peer already went away; nothing left to drain.
        }
    }

    public void Dispose()
    {
        disposed = true;
        listener.Stop();
    }
}

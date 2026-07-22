using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
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
    private readonly X509Certificate2 certificate;
    private Func<Http2RawFrame.Connection, Task> handler;
    private bool disposed;

    public Http2RawOriginServer(X509Certificate2 certificate)
    {
        this.certificate = certificate;
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public string Url => $"https://localhost:{Port}/";

    /// <summary>
    ///     Sets the handler invoked for each accepted connection, after the TLS/ALPN handshake and the
    ///     client connection preface have already been consumed.
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

            _ = Task.Run(async () =>
            {
                try
                {
                    var sslStream = new SslStream(client.GetStream(), false);
                    await sslStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = certificate,
                        ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http2 },
                        EnabledSslProtocols = System.Security.Authentication.SslProtocols.None
                    });

                    var preface = new byte[Http2Helper.ConnectionPreface.Length];
                    await Http2RawFrame.ReadExactAsync(sslStream, preface, 0, preface.Length);

                    var connection = new Http2RawFrame.Connection(sslStream);
                    var currentHandler = handler;
                    if (currentHandler != null)
                    {
                        await currentHandler(connection);
                    }
                }
                catch (Exception ex)
                {
                    // swallow - test assertions on the client/proxy side will surface the failure, but log
                    // for diagnostics since an exception here otherwise fails silently.
                    Console.WriteLine("Http2RawOriginServer connection handler failed: " + ex);
                }
                finally
                {
                    client.Dispose();
                }
            });
        }
    }

    public void Dispose()
    {
        disposed = true;
        listener.Stop();
    }
}

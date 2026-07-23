using System;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.IntegrationTests.Helpers;

/// <summary>
///     A minimal raw TLS/HTTP-1.1-only origin - unlike <see cref="Http2RawOriginServer" />, whose ALPN offer
///     is always exactly "h2", this never advertises "h2" at all, so it can stand in for a real-world origin
///     that genuinely does not support HTTP/2.
/// </summary>
internal sealed class Http11OnlyOriginServer : IDisposable
{
    private readonly TcpListener listener;
    private readonly X509Certificate2 certificate;
    private bool disposed;

    public Http11OnlyOriginServer(X509Certificate2 certificate)
    {
        this.certificate = certificate;
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

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
                        ApplicationProtocols =
                            new System.Collections.Generic.List<SslApplicationProtocol>
                                { SslApplicationProtocol.Http11 },
                        EnabledSslProtocols = SslProtocols.None
                    });

                    using var reader = new System.IO.StreamReader(sslStream, Encoding.ASCII, false, 4096, true);
                    string line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                    {
                        // drain request headers
                    }

                    var body = "h1-only-origin-ok";
                    var response = "HTTP/1.1 200 OK\r\n" +
                                   $"Content-Length: {Encoding.ASCII.GetByteCount(body)}\r\n" +
                                   "Connection: close\r\n\r\n" + body;
                    var responseBytes = Encoding.ASCII.GetBytes(response);
                    await sslStream.WriteAsync(responseBytes, 0, responseBytes.Length);
                }
                catch
                {
                    // best-effort test double; failures surface via the test's own assertions.
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

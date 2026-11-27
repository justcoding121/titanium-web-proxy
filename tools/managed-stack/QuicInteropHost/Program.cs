using System;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Quic;

int port = args.Length > 0 && int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) ? parsed : 4433;
if (port is 8000 or 8866 or 9080 or 9464 || port is < 1 or > 65535)
{
    Console.Error.WriteLine("Refusing port " + port.ToString(CultureInfo.InvariantCulture) + ".");
    return 1;
}

using X509Certificate2 certificate = Certificate();
await using ManagedQuicListener listener = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
{
    ListenEndPoint = new IPEndPoint(IPAddress.Any, port),
    HandshakeTimeout = TimeSpan.FromSeconds(10),
    ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new ManagedQuicServerConnectionOptions
    {
        ServerAuthenticationOptions = new SslServerAuthenticationOptions
        {
            ServerCertificate = certificate,
            ApplicationProtocols = [new SslApplicationProtocol("h3"), new SslApplicationProtocol("h3-29"), new SslApplicationProtocol("hq-interop"), new SslApplicationProtocol("hq-29")],
        },
    }),
}).ConfigureAwait(false);

Console.WriteLine("listening " + listener.LocalEndPoint);
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    stop.Cancel();
};

while (!stop.IsCancellationRequested)
{
    ManagedQuicConnection connection;
    try
    {
        connection = await listener.AcceptConnectionAsync(stop.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
        break;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
        continue;
    }

    Console.WriteLine("accepted " + connection.RemoteEndPoint);
    _ = HoldAsync(connection);
}

return 0;

static async Task HoldAsync(ManagedQuicConnection connection)
{
    try
    {
        await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        await connection.CloseAsync(0).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
    }
    finally
    {
        await connection.DisposeAsync().ConfigureAwait(false);
    }
}

static X509Certificate2 Certificate()
{
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
    return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
}

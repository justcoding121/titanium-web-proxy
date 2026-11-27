using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Quic;

int port = 4433;
string? www = null;
string? certs = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
    {
        port = parsed;
        i++;
    }
    else if (args[i] == "--www" && i + 1 < args.Length)
    {
        www = args[++i];
    }
    else if (args[i] == "--certs" && i + 1 < args.Length)
    {
        certs = args[++i];
    }
    else if (int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out int bare))
    {
        port = bare;
    }
}

string? testCase = Environment.GetEnvironmentVariable("TESTCASE");
if (testCase is not null && testCase is not ("handshake" or "handshakeloss"))
{
    Console.Error.WriteLine("unsupported test case: " + testCase);
    return 127;
}
if (port is 8000 or 8866 or 9080 or 9464 || port is < 1 or > 65535)
{
    Console.Error.WriteLine("Refusing port " + port.ToString(CultureInfo.InvariantCulture) + ".");
    return 1;
}

using var probe = new TcpListener(IPAddress.Any, port);
probe.Start();
using X509Certificate2 certificate = certs is null ? Certificate() : X509Certificate2.CreateFromPemFile(Path.Combine(certs, "cert.pem"), Path.Combine(certs, "priv.key"));
await using ManagedQuicListener listener = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
{
    ListenEndPoint = new IPEndPoint(IPAddress.Any, port),
    HandshakeTimeout = TimeSpan.FromSeconds(30),
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
    _ = www is null ? HoldAsync(connection) : ServeAsync(connection, www);
}

return 0;

static async Task ServeAsync(ManagedQuicConnection connection, string root)
{
    try
    {
        await using (connection)
        {
            ManagedQuicStream stream = await connection.AcceptInboundStreamAsync().ConfigureAwait(false);
            byte[] buffer = new byte[1024];
            var request = new StringBuilder();
            while (request.Length < 4096 && request.ToString().IndexOf("\r\n", StringComparison.Ordinal) < 0)
            {
                int count = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                request.Append(Encoding.ASCII.GetString(buffer, 0, count));
            }

            string? file = FileFor(root, request.ToString());
            if (file is not null)
            {
                byte[] body = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
                if (body.Length > 0)
                {
                    await stream.WriteAsync(body).ConfigureAwait(false);
                }
            }

            await stream.CompleteWritesAsync().ConfigureAwait(false);
            await connection.CloseAsync(0).ConfigureAwait(false);
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
    }
}

static string? FileFor(string root, string request)
{
    int end = request.IndexOf("\r\n", StringComparison.Ordinal);
    if (end < 5 || !request.StartsWith("GET /", StringComparison.Ordinal))
    {
        return null;
    }

    string raw = request[4..end].Trim();
    if (raw.Contains("..", StringComparison.Ordinal) || raw.Contains('\\'))
    {
        return null;
    }

    string relative = raw.TrimStart('/');
    string baseDir = Path.GetFullPath(root);
    string full = Path.GetFullPath(Path.Combine(baseDir, relative));
    string prefix = baseDir.EndsWith(Path.DirectorySeparatorChar) ? baseDir : baseDir + Path.DirectorySeparatorChar;
    if (!full.StartsWith(prefix, StringComparison.Ordinal) || !File.Exists(full))
    {
        return null;
    }

    return full;
}

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

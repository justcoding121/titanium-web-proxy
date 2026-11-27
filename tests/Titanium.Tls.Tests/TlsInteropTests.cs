using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Tls.Tests.Support;

namespace Titanium.Tls.Tests;

[TestClass]
[DoNotParallelize]
public sealed class TlsInteropTests
{
    public static IEnumerable<object[]> PairwiseRows
    {
        get
        {
            foreach (InteropCase row in InteropMatrix.Pairwise())
            {
                yield return new object[] { row.ToString() };
            }
        }
    }

    [TestMethod]
    [DynamicData(nameof(PairwiseRows))]
    [Timeout(60000)]
    public async Task PairwiseCellSpeaksTls13(string label)
    {
        await Exchange(InteropCase.Parse(label), 32);
    }

    [TestMethod]
    [Timeout(120000)]
    public async Task OpenSslEchoesTenMebibytes()
    {
        var row = new InteropCase(InteropRole.ManagedClient, InteropCipher.Aes128, InteropGroup.X25519, InteropCert.EcdsaP256, InteropAlpn.None, false, false);
        await Exchange(row, 10 * 1024 * 1024);
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task ResumesASecondConnection()
    {
        using X509Certificate2 certificate = Issue(InteropCert.EcdsaP256, client: false).Certificate;
        using var tickets = new MemoryTicketProtector();
        var cache = new MemoryTlsSessionCache();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Task server = Task.Run(async () =>
        {
            for (int i = 0; i < 2; i++)
            {
                using TcpClient accepted = await listener.AcceptTcpClientAsync();
                await using var stream = new TlsStream(accepted.GetStream(), new TlsStreamOptions { Tickets = tickets });
                await stream.AuthenticateAsServerAsync(ServerOptions(certificate));
                if (i == 1)
                {
                    Assert.IsTrue(stream.AcceptedResumption);
                }

                byte[] buffer = new byte[4];
                Assert.AreEqual(4, await stream.ReadAsync(buffer));
                await stream.WriteAsync(buffer);
            }
        });

        for (int i = 0; i < 2; i++)
        {
            using var tcp = new TcpClient();
            tcp.Connect((IPEndPoint)listener.LocalEndpoint);
            await using var stream = new TlsStream(tcp.GetStream(), new TlsStreamOptions { SessionCache = cache });
            await stream.AuthenticateAsClientAsync(ClientOptions(null, null));
            await stream.WriteAsync(new byte[] { 1, 2, 3, 4 });
            byte[] echoed = new byte[4];
            Assert.AreEqual(4, await ReadExact(stream, echoed));
        }

        await server;
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task ClientReadsAKestrelResponse()
    {
        PlatformSsl.RequireTls13();
        using X509Certificate2 certificate = Issue(InteropCert.EcdsaP256, client: false).Certificate;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate));
        });
        WebApplication app = builder.Build();
        app.MapGet("/", () => "ok");
        await app.StartAsync();
        try
        {
            string? address = null;
            foreach (string candidate in app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses)
            {
                address = candidate;
            }

            if (address is null)
            {
                throw new InvalidOperationException("Kestrel did not publish an address.");
            }

            int port = new Uri(address).Port;
            using var tcp = new TcpClient();
            tcp.Connect(IPAddress.Loopback, port);
            await using var stream = new TlsStream(tcp.GetStream());
            SslClientAuthenticationOptions options = ClientOptions("http/1.1", null);
            await stream.AuthenticateAsClientAsync(options);
            await stream.WriteAsync("GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"u8.ToArray());
            var response = new byte[1024];
            int count = 0;
            while (count < response.Length)
            {
                int read = await stream.ReadAsync(response.AsMemory(count));
                if (read == 0)
                {
                    break;
                }

                count += read;
                if (Encoding.ASCII.GetString(response, 0, count).Contains("ok", StringComparison.Ordinal))
                {
                    return;
                }
            }

            Assert.Fail(Encoding.ASCII.GetString(response, 0, count));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [TestMethod]
    [TestCategory("Slow")]
    public async Task FullMatrixSpeaksTls13()
    {
        foreach (InteropCase row in InteropMatrix.Full())
        {
            try
            {
                await Exchange(row, 32);
            }
            catch (Exception error)
            {
                throw new AssertFailedException(row + " " + error.Message, error);
            }
        }
    }

    [TestMethod]
    [Timeout(14400000)]
    public async Task SeededSampleAgreesWithOpenSsl()
    {
        int count = Positive(Environment.GetEnvironmentVariable("TITANIUM_FUZZ_HANDSHAKES"), 500);
        int seed = Environment.GetEnvironmentVariable("TITANIUM_FUZZ_SEED") is string text && int.TryParse(text, out int parsed)
            ? parsed
            : 8446;
        var random = new Random(seed);
        for (int index = 0; index < count; index++)
        {
            InteropCase row = DrawSupported(random);
            try
            {
                await Exchange(row, 32);
            }
            catch (Exception error)
            {
                throw new AssertFailedException("seed=" + seed + " index=" + index + " " + row, error);
            }
        }
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task FipsPolicySpeaksTls13WithOpenSsl()
    {
        var client = new InteropCase(InteropRole.ManagedClient, InteropCipher.Aes128, InteropGroup.P256, InteropCert.EcdsaP256, InteropAlpn.None, false, false);
        var server = new InteropCase(InteropRole.ManagedServer, InteropCipher.Aes128, InteropGroup.P256, InteropCert.EcdsaP256, InteropAlpn.None, false, false);
        await Exchange(client, 32, TlsAlgorithmPolicy.FipsApprovedOnly);
        await Exchange(server, 32, TlsAlgorithmPolicy.FipsApprovedOnly);
    }

    private static InteropCase DrawSupported(Random random)
    {
        if (random.Next(2) == 0)
        {
            var group = random.Next(2) == 0 ? InteropGroup.X25519 : InteropGroup.P256;
            return new InteropCase(
                InteropRole.ManagedClient,
                InteropCipher.Aes128,
                group,
                InteropCert.EcdsaP256,
                (InteropAlpn)random.Next(3),
                random.Next(2) == 1,
                group == InteropGroup.P256);
        }

        return new InteropCase(
            InteropRole.ManagedServer,
            (InteropCipher)random.Next(3),
            (InteropGroup)random.Next(4),
            (InteropCert)random.Next(3),
            (InteropAlpn)random.Next(3),
            random.Next(2) == 1,
            random.Next(2) == 1);
    }

    private static int Positive(string? text, int fallback) => int.TryParse(text, out int value) && value > 0 ? value : fallback;

    private static async Task Exchange(InteropCase row, int payloadLength) => await Exchange(row, payloadLength, TlsAlgorithmPolicy.Default);

    private static async Task Exchange(InteropCase row, int payloadLength, TlsAlgorithmPolicy policy)
    {
        if (!OpenSslOracle.Require(new Version(3, 5)))
        {
            Assert.Inconclusive("OpenSSL 3.5 or newer was not found.");
        }

        string openssl = OpenSslOracle.Find() ?? throw new InvalidOperationException("OpenSSL was not found.");
        string directory = Path.Combine(Path.GetTempPath(), "twp-interop-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        Issued server = Issue(row.Certificate, client: false);
        Issued? client = row.ClientAuthentication ? Issue(InteropCert.EcdsaP256, client: true) : null;
        try
        {
            string serverCert = Path.Combine(directory, "server.pem");
            string serverKey = Path.Combine(directory, "server.key");
            WritePem(serverCert, "CERTIFICATE", server.Certificate.RawData);
            WritePem(serverKey, "PRIVATE KEY", server.Key);
            string? clientCert = null;
            string? clientKey = null;
            if (client is not null)
            {
                clientCert = Path.Combine(directory, "client.pem");
                clientKey = Path.Combine(directory, "client.key");
                WritePem(clientCert, "CERTIFICATE", client.Value.Certificate.RawData);
                WritePem(clientKey, "PRIVATE KEY", client.Value.Key);
            }

            byte[] payload = new byte[payloadLength];
            payload.AsSpan().Fill(0x5A);
            if (row.Role == InteropRole.ManagedClient)
            {
                await ClientAgainstOpenSsl(openssl, row, server.Certificate, serverCert, serverKey, client?.Certificate, clientCert, payload, policy);
            }
            else
            {
                await ServerAgainstOpenSsl(openssl, row, server.Certificate, client?.Certificate, clientCert, clientKey, payload, policy);
            }
        }
        finally
        {
            server.Certificate.Dispose();
            client?.Certificate.Dispose();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task ClientAgainstOpenSsl(string openssl, InteropCase row, X509Certificate2 serverCertificate, string serverCert, string serverKey, X509Certificate2? clientCertificate, string? clientCert, byte[] payload, TlsAlgorithmPolicy policy = TlsAlgorithmPolicy.Default)
    {
        _ = serverCertificate;
        int port = FreePort();
        string groups = row.HelloRetry || row.Group == InteropGroup.P256 ? "P-256" : GroupName(row.Group);
        var args = new StringBuilder();
        args.Append("s_server -tls1_3 -quiet -num_tickets 0 -accept 127.0.0.1:").Append(port);
        args.Append(" -cert ").Append(Quote(serverCert)).Append(" -key ").Append(Quote(serverKey));
        args.Append(" -groups ").Append(groups);
        args.Append(" -ciphersuites ").Append(CipherName(row.Cipher));
        AppendAlpn(args, row.Alpn);
        if (clientCert is not null)
        {
            args.Append(" -Verify 1 -verify_return_error -CAfile ").Append(Quote(clientCert));
        }

        using Peer peer = Peer.Start(openssl, args.ToString(), drainOutput: false);
        using var tcp = new TcpClient();
        await Connect(tcp, port, peer);
        await using var stream = new TlsStream(tcp.GetStream(), new TlsStreamOptions { AlgorithmPolicy = policy });
        await stream.AuthenticateAsClientAsync(ClientOptions(AlpnName(row.Alpn), clientCertificate));
        if (AlpnName(row.Alpn) is string selected)
        {
            Assert.AreEqual(selected, stream.NegotiatedApplicationProtocol.ToString(), peer.ErrorText());
        }

        // s_server copies the TLS payload to stdout and copies stdin back onto the TLS connection.
        byte[] received = new byte[payload.Length];
        Task<int> fromPeer = ReadExact(peer.Output, received);
        await stream.WriteAsync(payload);
        Assert.AreEqual(payload.Length, await fromPeer, peer.ErrorText());
        CollectionAssert.AreEqual(payload, received, peer.ErrorText());
        byte[] returned = new byte[payload.Length];
        Task<int> fromStream = ReadExact(stream, returned);
        await peer.Input.WriteAsync(received);
        await peer.Input.FlushAsync();
        Assert.AreEqual(payload.Length, await fromStream, peer.ErrorText());
        CollectionAssert.AreEqual(payload, returned, peer.ErrorText());
    }

    private static async Task ServerAgainstOpenSsl(string openssl, InteropCase row, X509Certificate2 serverCertificate, X509Certificate2? clientCertificate, string? clientCert, string? clientKey, byte[] payload, TlsAlgorithmPolicy policy = TlsAlgorithmPolicy.Default)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var args = new StringBuilder();
        args.Append("s_client -tls1_3 -quiet -connect 127.0.0.1:").Append(port);
        args.Append(" -groups ").Append(row.HelloRetry ? "X25519:P-256" : GroupName(row.Group));
        args.Append(" -ciphersuites ").Append(CipherName(row.Cipher));
        AppendAlpn(args, row.Alpn);
        if (clientCert is not null && clientKey is not null)
        {
            args.Append(" -cert ").Append(Quote(clientCert)).Append(" -key ").Append(Quote(clientKey));
        }

        using Peer peer = Peer.Start(openssl, args.ToString(), drainOutput: false);
        using TcpClient accepted = await listener.AcceptTcpClientAsync();
        var streamOptions = new TlsStreamOptions
        {
            Groups = ServerGroups(row),
            CipherSuites = [CipherId(row.Cipher)],
            RequestClientCertificate = row.ClientAuthentication,
            AlgorithmPolicy = policy,
        };
        if (clientCertificate is not null)
        {
            streamOptions.ClientTrustAnchors = [clientCertificate];
        }

        await using var stream = new TlsStream(accepted.GetStream(), streamOptions);
        await stream.AuthenticateAsServerAsync(ServerOptions(serverCertificate, AlpnName(row.Alpn)));
        if (AlpnName(row.Alpn) is string selected)
        {
            Assert.AreEqual(selected, stream.NegotiatedApplicationProtocol.ToString(), peer.ErrorText());
        }

        await peer.Input.WriteAsync(payload);
        await peer.Input.FlushAsync();
        byte[] got = new byte[payload.Length];
        Assert.AreEqual(payload.Length, await ReadExact(stream, got), peer.ErrorText());
        CollectionAssert.AreEqual(payload, got, peer.ErrorText());
        await stream.WriteAsync(got);
        byte[] reply = new byte[payload.Length];
        Assert.AreEqual(payload.Length, await ReadExact(peer.Output, reply), peer.ErrorText());
        CollectionAssert.AreEqual(payload, reply, peer.ErrorText());
    }

    private static SslClientAuthenticationOptions ClientOptions(string? alpn, X509Certificate2? clientCertificate)
    {
        var options = new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
            EnabledSslProtocols = SslProtocols.Tls13,
            RemoteCertificateValidationCallback = static (_, _, _, _) => true,
        };
        if (alpn is not null)
        {
            options.ApplicationProtocols = [new SslApplicationProtocol(alpn)];
        }

        if (clientCertificate is not null)
        {
            options.ClientCertificates = [clientCertificate];
        }

        return options;
    }

    private static SslServerAuthenticationOptions ServerOptions(X509Certificate2 certificate, string? alpn = null)
    {
        var options = new SslServerAuthenticationOptions
        {
            ServerCertificate = certificate,
            EnabledSslProtocols = SslProtocols.Tls13,
        };
        if (alpn is not null)
        {
            options.ApplicationProtocols = [new SslApplicationProtocol(alpn)];
        }

        return options;
    }

    private static NamedGroup[] ServerGroups(InteropCase row)
    {
        if (row.HelloRetry)
        {
            return [NamedGroup.Secp256r1];
        }

        return row.Group switch
        {
            InteropGroup.X25519 => [NamedGroup.X25519],
            InteropGroup.P256 => [NamedGroup.Secp256r1],
            InteropGroup.P384 => [NamedGroup.Secp384r1],
            _ => [NamedGroup.X25519MlKem768],
        };
    }

    private static async Task Connect(TcpClient tcp, int port, Peer peer)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            if (peer.HasExited)
            {
                throw new InvalidOperationException(peer.ErrorText());
            }

            try
            {
                await tcp.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(50);
            }
        }

        throw new TimeoutException(peer.ErrorText());
    }

    private static async Task<int> ReadExact(Stream stream, byte[] buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset));
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        return offset;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void AppendAlpn(StringBuilder args, InteropAlpn alpn)
    {
        if (AlpnName(alpn) is string name)
        {
            args.Append(" -alpn ").Append(name);
        }
    }

    private static string? AlpnName(InteropAlpn alpn) => alpn switch
    {
        InteropAlpn.H2 => "h2",
        InteropAlpn.Http11 => "http/1.1",
        _ => null,
    };

    private static string GroupName(InteropGroup group) => group switch
    {
        InteropGroup.X25519 => "X25519",
        InteropGroup.P256 => "P-256",
        InteropGroup.P384 => "P-384",
        _ => "X25519MLKEM768",
    };

    private static string CipherName(InteropCipher cipher) => cipher switch
    {
        InteropCipher.Aes128 => "TLS_AES_128_GCM_SHA256",
        InteropCipher.Aes256 => "TLS_AES_256_GCM_SHA384",
        _ => "TLS_CHACHA20_POLY1305_SHA256",
    };

    private static ushort CipherId(InteropCipher cipher) => cipher switch
    {
        InteropCipher.Aes128 => 0x1301,
        InteropCipher.Aes256 => 0x1302,
        _ => 0x1303,
    };

    private static string Quote(string path) => "\"" + path + "\"";

    private static void WritePem(string path, string label, byte[] der)
    {
        File.WriteAllText(path, new string(PemEncoding.Write(label, der)));
    }

    private static Issued Issue(InteropCert kind, bool client)
    {
        using AsymmetricAlgorithm key = kind switch
        {
            InteropCert.Rsa2048 => RSA.Create(2048),
            InteropCert.EcdsaP384 => ECDsa.Create(ECCurve.NamedCurves.nistP384),
            _ => ECDsa.Create(ECCurve.NamedCurves.nistP256),
        };
        CertificateRequest request = key switch
        {
            ECDsa ecdsa => new CertificateRequest("CN=localhost", ecdsa, HashAlgorithmName.SHA256),
            RSA rsa => new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
            _ => throw new InvalidOperationException("Unexpected key."),
        };
        var usages = new OidCollection { new Oid(client ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, critical: false));
        using X509Certificate2 created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        byte[] pkcs8 = key switch
        {
            ECDsa ecdsa => ecdsa.ExportPkcs8PrivateKey(),
            RSA rsa => rsa.ExportPkcs8PrivateKey(),
            _ => throw new InvalidOperationException("Unexpected key."),
        };
        X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), password: null, X509KeyStorageFlags.Exportable);
        return new Issued(certificate, pkcs8);
    }

    private readonly struct Issued(X509Certificate2 certificate, byte[] key)
    {
        internal X509Certificate2 Certificate { get; } = certificate;

        internal byte[] Key { get; } = key;
    }

    private sealed class Peer : IDisposable
    {
        private readonly Process _process;
        private readonly StringBuilder _error = new();
        private readonly Task _errorTask;
        private readonly Task? _discard;

        private Peer(Process process, bool drainOutput)
        {
            _process = process;
            Input = process.StandardInput.BaseStream;
            Output = process.StandardOutput.BaseStream;
            _errorTask = Task.Run(ReadError);
            if (drainOutput)
            {
                _discard = Task.Run(DiscardOutput);
            }
        }

        internal Stream Input { get; }

        internal Stream Output { get; }

        internal bool HasExited => _process.HasExited;

        internal static Peer Start(string openssl, string arguments, bool drainOutput)
        {
            var start = new ProcessStartInfo
            {
                FileName = openssl,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            Process process = Process.Start(start) ?? throw new InvalidOperationException("OpenSSL did not start.");
            return new Peer(process, drainOutput);
        }

        internal string ErrorText()
        {
            return _error.ToString();
        }

        public void Dispose()
        {
            try
            {
                Input.Dispose();
            }
            catch (IOException)
            {
            }

            if (!_process.HasExited && !_process.WaitForExit(500))
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(2000);
            }

            _errorTask.Wait(2000);
            _discard?.Wait(2000);
            _process.Dispose();
        }

        private void ReadError()
        {
            var buffer = new char[256];
            while (true)
            {
                int read = _process.StandardError.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    return;
                }

                _error.Append(buffer, 0, read);
            }
        }

        private void DiscardOutput()
        {
            var buffer = new byte[4096];
            while (Output.Read(buffer, 0, buffer.Length) > 0)
            {
            }
        }
    }
}

internal enum InteropRole
{
    ManagedClient,
    ManagedServer,
}

internal enum InteropCipher
{
    Aes128,
    Aes256,
    ChaCha,
}

internal enum InteropGroup
{
    X25519,
    P256,
    P384,
    Hybrid,
}

internal enum InteropCert
{
    EcdsaP256,
    EcdsaP384,
    Rsa2048,
}

internal enum InteropAlpn
{
    H2,
    Http11,
    None,
}

internal readonly record struct InteropCase(InteropRole Role, InteropCipher Cipher, InteropGroup Group, InteropCert Certificate, InteropAlpn Alpn, bool ClientAuthentication, bool HelloRetry)
{
    public override string ToString()
    {
        return Role + "," + Cipher + "," + Group + "," + Certificate + "," + Alpn + "," + ClientAuthentication + "," + HelloRetry;
    }

    internal static InteropCase Parse(string text)
    {
        string[] parts = text.Split(',');
        return new InteropCase(
            Enum.Parse<InteropRole>(parts[0]),
            Enum.Parse<InteropCipher>(parts[1]),
            Enum.Parse<InteropGroup>(parts[2]),
            Enum.Parse<InteropCert>(parts[3]),
            Enum.Parse<InteropAlpn>(parts[4]),
            bool.Parse(parts[5]),
            bool.Parse(parts[6]));
    }
}

internal static class InteropMatrix
{
    internal static IReadOnlyList<InteropCase> Pairwise()
    {
        var rows = new List<InteropCase>();
        foreach (int[] row in Cover([2, 3, 2], 17))
        {
            var group = (InteropGroup)row[0];
            rows.Add(new InteropCase(InteropRole.ManagedClient, InteropCipher.Aes128, group, InteropCert.EcdsaP256, (InteropAlpn)row[1], row[2] == 1, group == InteropGroup.P256));
        }

        foreach (int[] row in Cover([3, 4, 3, 3, 2, 2], 17))
        {
            rows.Add(new InteropCase(
                InteropRole.ManagedServer,
                (InteropCipher)row[0],
                (InteropGroup)row[1],
                (InteropCert)row[2],
                (InteropAlpn)row[3],
                row[4] == 1,
                row[5] == 1));
        }

        return rows;
    }

    internal static IReadOnlyList<InteropCase> Full()
    {
        var rows = new List<InteropCase>();
        foreach (InteropGroup group in Enum.GetValues<InteropGroup>())
        {
            if (group is InteropGroup.P384 or InteropGroup.Hybrid)
            {
                continue;
            }

            foreach (InteropAlpn alpn in Enum.GetValues<InteropAlpn>())
            {
                rows.Add(new InteropCase(InteropRole.ManagedClient, InteropCipher.Aes128, group, InteropCert.EcdsaP256, alpn, false, group == InteropGroup.P256));
                rows.Add(new InteropCase(InteropRole.ManagedClient, InteropCipher.Aes128, group, InteropCert.EcdsaP256, alpn, true, group == InteropGroup.P256));
            }
        }

        foreach (InteropCipher cipher in Enum.GetValues<InteropCipher>())
        {
            foreach (InteropGroup group in Enum.GetValues<InteropGroup>())
            {
                foreach (InteropCert certificate in Enum.GetValues<InteropCert>())
                {
                    foreach (InteropAlpn alpn in Enum.GetValues<InteropAlpn>())
                    {
                        foreach (bool auth in new[] { false, true })
                        {
                            foreach (bool retry in new[] { false, true })
                            {
                                rows.Add(new InteropCase(InteropRole.ManagedServer, cipher, group, certificate, alpn, auth, retry));
                            }
                        }
                    }
                }
            }
        }

        return rows;
    }

    private static List<int[]> Cover(int[] sizes, int seed)
    {
        var pairs = new List<(int Left, int LeftValue, int Right, int RightValue)>();
        for (int left = 0; left < sizes.Length; left++)
        {
            for (int right = left + 1; right < sizes.Length; right++)
            {
                for (int leftValue = 0; leftValue < sizes[left]; leftValue++)
                {
                    for (int rightValue = 0; rightValue < sizes[right]; rightValue++)
                    {
                        pairs.Add((left, leftValue, right, rightValue));
                    }
                }
            }
        }

        var random = new Random(seed);
        var open = new bool[pairs.Count];
        Array.Fill(open, true);
        int remaining = pairs.Count;
        var rows = new List<int[]>();
        while (remaining > 0)
        {
            int[] best = new int[sizes.Length];
            int bestHits = 0;
            for (int attempt = 0; attempt < 64; attempt++)
            {
                var candidate = new int[sizes.Length];
                for (int factor = 0; factor < sizes.Length; factor++)
                {
                    candidate[factor] = random.Next(sizes[factor]);
                }

                int hits = Hits(candidate, pairs, open);
                if (hits > bestHits)
                {
                    bestHits = hits;
                    candidate.CopyTo(best, 0);
                }
            }

            if (bestHits == 0)
            {
                for (int index = 0; index < pairs.Count; index++)
                {
                    if (!open[index])
                    {
                        continue;
                    }

                    for (int factor = 0; factor < sizes.Length; factor++)
                    {
                        best[factor] = random.Next(sizes[factor]);
                    }

                    best[pairs[index].Left] = pairs[index].LeftValue;
                    best[pairs[index].Right] = pairs[index].RightValue;
                    break;
                }
            }

            rows.Add((int[])best.Clone());
            for (int index = 0; index < pairs.Count; index++)
            {
                if (open[index] && Covers(best, pairs[index]))
                {
                    open[index] = false;
                    remaining--;
                }
            }
        }

        return rows;
    }

    private static int Hits(int[] row, List<(int Left, int LeftValue, int Right, int RightValue)> pairs, bool[] open)
    {
        int hits = 0;
        for (int index = 0; index < pairs.Count; index++)
        {
            if (open[index] && Covers(row, pairs[index]))
            {
                hits++;
            }
        }

        return hits;
    }

    private static bool Covers(int[] row, (int Left, int LeftValue, int Right, int RightValue) pair)
    {
        return row[pair.Left] == pair.LeftValue && row[pair.Right] == pair.RightValue;
    }
}

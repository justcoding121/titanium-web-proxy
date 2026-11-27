using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Tls.Tests.Support;

namespace Titanium.Tls.Tests;

[TestClass]
[DoNotParallelize]
public sealed class HandshakeOracleTests
{
    [TestMethod]
    [Timeout(120000)]
    public void MutatedClientHelloAgreesWithOpenSsl()
    {
        if (!OpenSslOracle.Require(new Version(3, 5)))
        {
            Assert.Inconclusive("OpenSSL 3.5 or newer was not found.");
        }

        string openssl = OpenSslOracle.Find() ?? throw new InvalidOperationException("OpenSSL was not found.");
        string directory = Path.Combine(Path.GetTempPath(), "twp-oracle-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        string certificate = Path.Combine(directory, "server.pem");
        string key = Path.Combine(directory, "server.key");
        WriteCertificate(certificate, key);
        byte[] hello = ClientHello();
        (string Name, byte[] Bytes)[] rows =
        [
            ("intact", hello),
            ("truncated", hello[..8]),
            ("empty-record", EmptyRecord()),
            ("thirty-three-empty-records", Repeat(EmptyRecord(), 33)),
            ("handshake-type", Change(hello, 5, 11)),
            ("tls12-only", Versions(hello, 0x03, 0x03)),
            ("duplicate-extension", DuplicateFirstExtension(hello)),
            ("extension-length", StretchExtension(hello)),
        ];
        var mismatches = new StringBuilder();
        try
        {
            foreach ((string name, byte[] bytes) in rows)
            {
                byte managed = ProbeManaged(bytes);
                byte peer = ProbeOpenSsl(openssl, certificate, key, bytes);
                if (managed != peer)
                {
                    mismatches.Append(name).Append(" managed=").Append(managed).Append(" openssl=").Append(peer).AppendLine();
                }
            }
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        if (mismatches.Length > 0)
        {
            Assert.Fail(mismatches.ToString());
        }
    }

    private static byte ProbeManaged(byte[] hello)
    {
        using var engine = new TlsEngine(TlsRole.Server);
        var output = new TlsOutput();
        engine.Advance(hello, out _, output);
        return engine.State == TlsEngineState.Closed ? engine.LastLocalAlert : (byte)0;
    }

    private static byte ProbeOpenSsl(string openssl, string certificate, string key, byte[] hello)
    {
        int port = FreePort();
        var start = new ProcessStartInfo
        {
            FileName = openssl,
            Arguments = "s_server -tls1_3 -quiet -num_tickets 0 -naccept 1 -accept 127.0.0.1:" + port + " -cert \"" + certificate + "\" -key \"" + key + "\"",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("OpenSSL did not start.");
        try
        {
            using var tcp = new TcpClient();
            Connect(tcp, port, process);
            NetworkStream stream = tcp.GetStream();
            stream.Write(hello);
            stream.Flush();
            var header = new byte[5];
            int read = Read(stream, header, 2000, out bool timedOut);
            if (read < 5)
            {
                return timedOut && !process.HasExited ? (byte)0 : (byte)0xFE;
            }

            if (header[0] == 21)
            {
                var body = new byte[2];
                if (Read(stream, body, 2000, out _) < 2)
                {
                    return 0xFE;
                }

                return body[1];
            }

            return header[0] == 22 ? (byte)0 : (byte)0xFD;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
        }
    }

    private static byte[] ClientHello()
    {
        using var engine = new TlsEngine(TlsRole.Client, serverName: "localhost");
        var output = new TlsOutput();
        engine.Advance(ReadOnlySpan<byte>.Empty, out _, output);
        return output.Bytes.ToArray();
    }

    private static byte[] EmptyRecord() => [23, 3, 3, 0, 0];

    private static byte[] Repeat(byte[] unit, int count)
    {
        var buffer = new byte[unit.Length * count];
        for (int i = 0; i < count; i++)
        {
            unit.CopyTo(buffer.AsSpan(i * unit.Length));
        }

        return buffer;
    }

    private static byte[] Change(byte[] source, int index, byte value)
    {
        byte[] copy = [.. source];
        copy[index] = value;
        return copy;
    }

    private static byte[] Versions(byte[] source, byte major, byte minor)
    {
        byte[] copy = [.. source];
        int found = copy.AsSpan().IndexOf(new byte[] { 0x00, 0x2b, 0x00, 0x03, 0x02, 0x03, 0x04 });
        if (found < 0)
        {
            throw new InvalidOperationException("The hello has no supported_versions extension.");
        }

        copy[found + 5] = major;
        copy[found + 6] = minor;
        return copy;
    }

    private static byte[] DuplicateFirstExtension(byte[] record)
    {
        int cursor = 5 + 4 + 2 + 32;
        cursor += 1 + record[cursor];
        int cipherLength = (record[cursor] << 8) | record[cursor + 1];
        cursor += 2 + cipherLength;
        cursor += 1 + record[cursor];
        int extensionsAt = cursor;
        int extensionLength = (record[cursor] << 8) | record[cursor + 1];
        int start = cursor + 2;
        int firstLength = (record[start + 2] << 8) | record[start + 3];
        int first = 4 + firstLength;
        var copy = new byte[record.Length + first];
        record.AsSpan(0, start + extensionLength).CopyTo(copy);
        record.AsSpan(start, first).CopyTo(copy.AsSpan(start + extensionLength));
        record.AsSpan(start + extensionLength).CopyTo(copy.AsSpan(start + extensionLength + first));
        Add16(copy, 3, first);
        Add24(copy, 6, first);
        Add16(copy, extensionsAt, first);
        return copy;
    }

    private static byte[] StretchExtension(byte[] record)
    {
        byte[] copy = [.. record];
        int cursor = 5 + 4 + 2 + 32;
        cursor += 1 + copy[cursor];
        int cipherLength = (copy[cursor] << 8) | copy[cursor + 1];
        cursor += 2 + cipherLength;
        cursor += 1 + copy[cursor];
        int start = cursor + 2;
        copy[start + 2] = 0x7F;
        copy[start + 3] = 0xFF;
        return copy;
    }

    private static void Add16(byte[] buffer, int offset, int extra)
    {
        int value = ((buffer[offset] << 8) | buffer[offset + 1]) + extra;
        buffer[offset] = (byte)(value >> 8);
        buffer[offset + 1] = (byte)value;
    }

    private static void Add24(byte[] buffer, int offset, int extra)
    {
        int value = (buffer[offset] << 16) | (buffer[offset + 1] << 8) | buffer[offset + 2];
        value += extra;
        buffer[offset] = (byte)(value >> 16);
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)value;
    }

    private static void Connect(TcpClient tcp, int port, Process process)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException(process.StandardError.ReadToEnd());
            }

            try
            {
                tcp.Connect(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException)
            {
                Thread.Sleep(50);
            }
        }

        throw new TimeoutException(process.StandardError.ReadToEnd());
    }

    private static int Read(NetworkStream stream, byte[] buffer, int timeoutMilliseconds, out bool timedOut)
    {
        stream.ReadTimeout = timeoutMilliseconds;
        timedOut = false;
        int offset = 0;
        try
        {
            while (offset < buffer.Length)
            {
                int read = stream.Read(buffer, offset, buffer.Length - offset);
                if (read == 0)
                {
                    break;
                }

                offset += read;
            }
        }
        catch (IOException ex) when (ex.InnerException is SocketException socket && socket.SocketErrorCode == SocketError.TimedOut)
        {
            timedOut = true;
        }
        catch (IOException)
        {
        }

        return offset;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void WriteCertificate(string certificatePath, string keyPath)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, critical: false));
        using X509Certificate2 created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        File.WriteAllText(certificatePath, new string(PemEncoding.Write("CERTIFICATE", created.RawData)));
        File.WriteAllText(keyPath, new string(PemEncoding.Write("PRIVATE KEY", key.ExportPkcs8PrivateKey())));
    }
}

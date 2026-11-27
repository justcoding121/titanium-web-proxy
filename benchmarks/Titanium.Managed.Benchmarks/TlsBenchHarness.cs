using System;
using System.IO;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using Titanium.Tls;

namespace Titanium.Managed.Benchmarks;

public enum StackKind
{
    Platform,
    Managed,
}

internal static class TlsBenchHarness
{
    public const int BulkBytes = 256 * 1024 * 1024;

    public static X509Certificate2 CreateEcdsa() => CreateCertificate(rsa: false);

    public static X509Certificate2 CreateRsa() => CreateCertificate(rsa: true);

    public static OpenedConnection Open(StackKind stack, TransportKind transport, X509Certificate2 certificate, SessionGear? gear)
    {
        TransportPair pair = TransportPair.Create(transport);
        try
        {
            return Authenticate(stack, pair, certificate, gear);
        }
        catch
        {
            pair.Dispose();
            throw;
        }
    }

    public static void Transfer(Stream source, Stream destination, byte[] chunk, byte[] readBuffer, long total)
    {
        Exception? readerError = null;
        var reader = new Thread(() =>
        {
            try
            {
                ReadExactly(destination, readBuffer, total);
            }
            catch (Exception ex)
            {
                readerError = ex;
            }
        });
        reader.IsBackground = true;
        reader.Start();
        try
        {
            WriteRepeated(source, chunk, total);
        }
        finally
        {
            reader.Join();
        }

        if (readerError is not null)
        {
            throw readerError;
        }
    }

    public static void Exchange(Stream client, Stream server, byte[] payload)
    {
        var echo = new byte[payload.Length];
        Exception? serverError = null;
        var thread = new Thread(() =>
        {
            try
            {
                ReadFull(server, echo);
                server.Write(echo);
            }
            catch (Exception ex)
            {
                serverError = ex;
            }
        });
        thread.IsBackground = true;
        thread.Start();
        try
        {
            client.Write(payload);
            ReadFull(client, payload);
        }
        finally
        {
            thread.Join();
        }

        if (serverError is not null)
        {
            throw serverError;
        }
    }

    private static OpenedConnection Authenticate(StackKind stack, TransportPair pair, X509Certificate2 certificate, SessionGear? gear)
    {
        Stream? client = null;
        Stream? server = null;
        Exception? serverError = null;
        var serverThread = new Thread(() =>
        {
            try
            {
                server = AuthenticateServer(stack, pair.Server, certificate, gear);
            }
            catch (Exception ex)
            {
                serverError = ex;
            }
        });
        serverThread.IsBackground = true;
        serverThread.Start();
        try
        {
            client = AuthenticateClient(stack, pair.Client, gear);
            serverThread.Join();
        }
        catch
        {
            pair.Dispose();
            serverThread.Join();
            throw;
        }

        if (serverError is not null || client is null || server is null)
        {
            pair.Dispose();
            throw serverError ?? new InvalidOperationException("The handshake did not finish.");
        }

        bool resumed = server is TlsStream tls && tls.AcceptedResumption;
        return new OpenedConnection(pair, client, server, resumed);
    }

    private static Stream AuthenticateClient(StackKind stack, Stream inner, SessionGear? gear)
    {
        var options = new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
            EnabledSslProtocols = SslProtocols.Tls13,
            RemoteCertificateValidationCallback = static (_, _, _, _) => true,
        };
        if (stack == StackKind.Platform)
        {
            var ssl = new SslStream(inner, leaveInnerStreamOpen: false);
            ssl.AuthenticateAsClient(options);
            return ssl;
        }

        var stream = new TlsStream(inner, new TlsStreamOptions { SessionCache = gear?.Cache });
        stream.AuthenticateAsClient(options);
        return stream;
    }

    private static Stream AuthenticateServer(StackKind stack, Stream inner, X509Certificate2 certificate, SessionGear? gear)
    {
        var options = new SslServerAuthenticationOptions
        {
            ServerCertificate = certificate,
            EnabledSslProtocols = SslProtocols.Tls13,
        };
        if (stack == StackKind.Platform)
        {
            var ssl = new SslStream(inner, leaveInnerStreamOpen: false);
            ssl.AuthenticateAsServer(options);
            return ssl;
        }

        var stream = new TlsStream(inner, new TlsStreamOptions { Tickets = gear?.Tickets });
        stream.AuthenticateAsServer(options);
        return stream;
    }

    private static void WriteRepeated(Stream destination, byte[] buffer, long total)
    {
        long written = 0;
        while (written < total)
        {
            int length = (int)Math.Min(buffer.Length, total - written);
            destination.Write(buffer, 0, length);
            written += length;
        }
    }

    private static void ReadExactly(Stream source, byte[] buffer, long total)
    {
        long read = 0;
        while (read < total)
        {
            int length = (int)Math.Min(buffer.Length, total - read);
            int count = source.Read(buffer, 0, length);
            if (count == 0)
            {
                throw new EndOfStreamException("The transfer ended early.");
            }

            read += count;
        }
    }

    private static void ReadFull(Stream source, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int count = source.Read(buffer, read, buffer.Length - read);
            if (count == 0)
            {
                throw new EndOfStreamException("The exchange ended early.");
            }

            read += count;
        }
    }

    private static X509Certificate2 CreateCertificate(bool rsa)
    {
        using AsymmetricAlgorithm key = rsa ? RSA.Create(2048) : ECDsa.Create(ECCurve.NamedCurves.nistP256)!;
        CertificateRequest request = key switch
        {
            ECDsa ecdsa => new CertificateRequest("CN=localhost", ecdsa, HashAlgorithmName.SHA256),
            RSA algorithm => new CertificateRequest("CN=localhost", algorithm, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
            _ => throw new InvalidOperationException("The certificate key is missing."),
        };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, critical: false));
        using X509Certificate2 created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), password: null, X509KeyStorageFlags.Exportable);
    }
}

internal sealed class SessionGear : IDisposable
{
    public SessionGear()
    {
        Tickets = new MemoryTicketProtector();
        Cache = new MemoryTlsSessionCache();
    }

    public MemoryTicketProtector Tickets { get; }

    public MemoryTlsSessionCache Cache { get; }

    public void Dispose() => Tickets.Dispose();
}

internal sealed class OpenedConnection : IDisposable
{
    private readonly TransportPair _pair;
    private int _disposed;

    public OpenedConnection(TransportPair pair, Stream client, Stream server, bool resumed)
    {
        _pair = pair;
        Client = client;
        Server = server;
        Resumed = resumed;
    }

    public Stream Client { get; }

    public Stream Server { get; }

    public bool Resumed { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _pair.Unblock();
        try
        {
            Client.Dispose();
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            Server.Dispose();
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        _pair.Dispose();
    }
}

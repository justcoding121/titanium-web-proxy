using System;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Titanium.Tls;

/// <summary>What the server callback returns after it has seen the ClientHello.</summary>
internal sealed class TlsServerSelection
{
    internal required X509Certificate2 Certificate { get; init; }

    internal string[]? ApplicationProtocols { get; init; }

    internal bool RequireApplicationProtocol { get; init; }

    internal bool RequestClientCertificate { get; init; }

    /// <summary>An empty client Certificate is <c>certificate_required</c> (116). Resumption is refused while a certificate is requested.</summary>
    internal bool RequireClientCertificate { get; init; }

    internal RemoteCertificateValidationCallback? ClientCertificateCallback { get; init; }

    internal X509Certificate2Collection? ClientTrustAnchors { get; init; }

    internal NamedGroup[]? Groups { get; init; }

    internal ushort[]? CipherSuites { get; init; }

    internal bool PreferServerCipherOrder { get; init; }

    internal ITicketProtector? Tickets { get; init; }
}

/// <summary>The ClientHello the server callback is allowed to inspect.</summary>
internal sealed class TlsClientHelloInfo
{
    internal TlsClientHelloInfo(string? serverName, byte[] rawHello)
    {
        ServerName = serverName;
        RawHello = rawHello;
    }

    internal string? ServerName { get; }

    internal byte[] RawHello { get; }
}

/// <summary>Encrypts a session ticket. The default implementation rotates a key every 24 hours and still accepts the previous one.</summary>
internal interface ITicketProtector
{
    byte[] Protect(ReadOnlySpan<byte> plaintext);

    bool TryUnprotect(ReadOnlySpan<byte> ticket, Span<byte> destination, out int written);
}

/// <summary>In-memory AES-256-GCM tickets. Two keys are live: the current one and the one it replaced.</summary>
internal sealed class MemoryTicketProtector : ITicketProtector, IDisposable
{
    private readonly byte[][] _keys = [new byte[32], new byte[32]];
    private int _current;
    private long _born;
    private bool _disposed;

    internal MemoryTicketProtector()
    {
        RandomNumberGenerator.Fill(_keys[0]);
        RandomNumberGenerator.Fill(_keys[1]);
        _born = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    internal long UnixTimeSeconds { get; set; }

    internal void ForceRotate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _current = 1 - _current;
        RandomNumberGenerator.Fill(_keys[_current]);
        _born = Now();
    }

    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Now() - _born >= 24 * 60 * 60)
        {
            ForceRotate();
        }

        var ticket = new byte[1 + 12 + plaintext.Length + 16];
        ticket[0] = (byte)_current;
        RandomNumberGenerator.Fill(ticket.AsSpan(1, 12));
        using var aes = new AesGcm(_keys[_current], 16);
        aes.Encrypt(ticket.AsSpan(1, 12), plaintext, ticket.AsSpan(13, plaintext.Length), ticket.AsSpan(ticket.Length - 16));
        return ticket;
    }

    public bool TryUnprotect(ReadOnlySpan<byte> ticket, Span<byte> destination, out int written)
    {
        written = 0;
        if (_disposed || ticket.Length < 1 + 12 + 16)
        {
            return false;
        }

        int id = ticket[0];
        if (id > 1)
        {
            return false;
        }

        int plaintext = ticket.Length - 1 - 12 - 16;
        if (destination.Length < plaintext)
        {
            return false;
        }

        try
        {
            using var aes = new AesGcm(_keys[id], 16);
            aes.Decrypt(ticket.Slice(1, 12), ticket.Slice(13, plaintext), ticket.Slice(ticket.Length - 16), destination[..plaintext]);
        }
        catch (CryptographicException)
        {
            return false;
        }

        written = plaintext;
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CryptographicOperations.ZeroMemory(_keys[0]);
        CryptographicOperations.ZeroMemory(_keys[1]);
    }

    private long Now()
    {
        return UnixTimeSeconds > 0 ? UnixTimeSeconds : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }
}

using System;

namespace Titanium.Tls.Tests;

/// <summary>Entry point for a handshake fuzzer. A bad input closes or waits. It does not throw.</summary>
internal static class TlsFuzzTarget
{
    internal static void Handshake(byte[] data)
    {
        Drive(TlsRole.Client, data);
        Drive(TlsRole.Server, data);
        if (data.Length < 4)
        {
            return;
        }

        ReadOnlySpan<byte> message = data;
        _ = HandshakeMessages.TryReadClientHello(message, out _, out _);
        _ = HandshakeMessages.TryReadServerHello(message, out _, out _);
        _ = HandshakeMessages.TryReadEncryptedExtensions(message, out _, out _);
        _ = HandshakeMessages.TryReadCertificate(message, out _, out _);
        _ = HandshakeMessages.TryReadCertificateRequest(message, out _, out _, out _);
        _ = HandshakeMessages.TryReadCertificateVerify(message, out _, out _, out _);
        _ = HandshakeMessages.TryReadFinished(message, out _, out _);
        _ = HandshakeMessages.TryReadNewSessionTicket(message, out _, out _, out _, out _, out _, out _);
        _ = HandshakeMessages.TryReadKeyUpdate(message, out _, out _);
    }

    private static void Drive(TlsRole role, byte[] data)
    {
        using var engine = new TlsEngine(role);
        var output = new TlsOutput();
        engine.Advance(data, out _, output);
    }
}

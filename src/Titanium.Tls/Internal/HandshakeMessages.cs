using System;

namespace Titanium.Tls;

internal readonly ref struct ClientHelloView
{
    internal ClientHelloView(
        ushort legacyVersion,
        ReadOnlySpan<byte> random,
        ReadOnlySpan<byte> sessionId,
        ReadOnlySpan<byte> cipherSuites,
        ReadOnlySpan<byte> compressionMethods,
        ReadOnlySpan<byte> extensions,
        bool hasExtensions)
    {
        LegacyVersion = legacyVersion;
        Random = random;
        SessionId = sessionId;
        CipherSuites = cipherSuites;
        CompressionMethods = compressionMethods;
        Extensions = extensions;
        HasExtensions = hasExtensions;
    }

    internal ushort LegacyVersion { get; }

    internal ReadOnlySpan<byte> Random { get; }

    internal ReadOnlySpan<byte> SessionId { get; }

    internal ReadOnlySpan<byte> CipherSuites { get; }

    internal ReadOnlySpan<byte> CompressionMethods { get; }

    internal ReadOnlySpan<byte> Extensions { get; }

    internal bool HasExtensions { get; }

    internal bool TryWrite(Span<byte> destination, out int written)
    {
        written = 0;
        TlsWriter writer = TlsWriter.Over(destination);
        if (!writer.TryWriteUInt8((byte)HandshakeType.ClientHello) || !writer.TryReserveLength(3, out int lengthAt))
        {
            return false;
        }

        if (!writer.TryWriteUInt16(LegacyVersion) ||
            !writer.TryWrite(Random) ||
            !writer.TryWriteVector8(SessionId) ||
            !writer.TryWriteVector16(CipherSuites) ||
            !writer.TryWriteVector8(CompressionMethods) ||
            (HasExtensions && !writer.TryWriteVector16(Extensions)) ||
            !writer.TryPatchLength(lengthAt, 3))
        {
            return false;
        }

        written = writer.Written;
        return true;
    }
}

internal readonly ref struct ServerHelloView
{
    internal ServerHelloView(
        ushort legacyVersion,
        ReadOnlySpan<byte> random,
        ReadOnlySpan<byte> sessionId,
        ushort cipherSuite,
        byte compressionMethod,
        ReadOnlySpan<byte> extensions,
        bool helloRetryRequest)
    {
        LegacyVersion = legacyVersion;
        Random = random;
        SessionId = sessionId;
        CipherSuite = cipherSuite;
        CompressionMethod = compressionMethod;
        Extensions = extensions;
        IsHelloRetryRequest = helloRetryRequest;
    }

    internal ushort LegacyVersion { get; }

    internal ReadOnlySpan<byte> Random { get; }

    internal ReadOnlySpan<byte> SessionId { get; }

    internal ushort CipherSuite { get; }

    internal byte CompressionMethod { get; }

    internal ReadOnlySpan<byte> Extensions { get; }

    internal bool IsHelloRetryRequest { get; }

    internal bool TryWrite(Span<byte> destination, out int written)
    {
        written = 0;
        TlsWriter writer = TlsWriter.Over(destination);
        if (!writer.TryWriteUInt8((byte)HandshakeType.ServerHello) || !writer.TryReserveLength(3, out int lengthAt))
        {
            return false;
        }

        if (!writer.TryWriteUInt16(LegacyVersion) ||
            !writer.TryWrite(Random) ||
            !writer.TryWriteVector8(SessionId) ||
            !writer.TryWriteUInt16(CipherSuite) ||
            !writer.TryWriteUInt8(CompressionMethod) ||
            !writer.TryWriteVector16(Extensions) ||
            !writer.TryPatchLength(lengthAt, 3))
        {
            return false;
        }

        written = writer.Written;
        return true;
    }
}

internal readonly ref struct CertificateView
{
    internal CertificateView(ReadOnlySpan<byte> requestContext, ReadOnlySpan<byte> certificateList)
    {
        RequestContext = requestContext;
        CertificateList = certificateList;
    }

    internal ReadOnlySpan<byte> RequestContext { get; }

    internal ReadOnlySpan<byte> CertificateList { get; }

    internal bool TryWrite(Span<byte> destination, out int written)
    {
        return WriteFramed(destination, HandshakeType.Certificate, RequestContext, CertificateList, out written);
    }

    internal static bool WriteFramed(Span<byte> destination, HandshakeType type, ReadOnlySpan<byte> context, ReadOnlySpan<byte> list, out int written)
    {
        written = 0;
        TlsWriter writer = TlsWriter.Over(destination);
        if (!writer.TryWriteUInt8((byte)type) || !writer.TryReserveLength(3, out int lengthAt))
        {
            return false;
        }

        if (!writer.TryWriteVector8(context) || !writer.TryWriteVector24(list) || !writer.TryPatchLength(lengthAt, 3))
        {
            return false;
        }

        written = writer.Written;
        return true;
    }
}

internal static class HandshakeMessages
{
    /// <summary>One hundred entries still parse. The boundary test locks that. The next entry is <c>decode_error</c>.</summary>
    internal const int MaxCertificateEntries = 100;
    private static ReadOnlySpan<byte> HelloRetryRequestRandom =>
    [
        0xCF, 0x21, 0xAD, 0x74, 0xE5, 0x9A, 0x61, 0x11, 0xBE, 0x1D, 0x8C, 0x02, 0x1E, 0x65, 0xB8, 0x91,
        0xC2, 0xA2, 0x11, 0x16, 0x7A, 0xBB, 0x8C, 0x5E, 0x07, 0x9E, 0x09, 0xE2, 0xC8, 0xA8, 0x33, 0x9C,
    ];

    internal static bool TryReadClientHello(ReadOnlySpan<byte> message, out ClientHelloView view, out HandshakeAlert alert)
    {
        view = default;
        if (!TryBody(message, HandshakeType.ClientHello, out ReadOnlySpan<byte> body, out alert))
        {
            return false;
        }

        TlsReader reader = new(body);
        if (!reader.TryReadUInt16(out ushort version) ||
            !reader.TryReadBytes(32, out ReadOnlySpan<byte> random) ||
            !reader.TryReadVector8(out ReadOnlySpan<byte> sessionId) ||
            !reader.TryReadVector16(out ReadOnlySpan<byte> ciphers) ||
            !reader.TryReadVector8(out ReadOnlySpan<byte> compression))
        {
            alert = HandshakeAlert.DecodeError;
            return false;
        }

        if (sessionId.Length > 32 || ciphers.Length < 2 || (ciphers.Length & 1) != 0 || compression.IsEmpty)
        {
            alert = HandshakeAlert.DecodeError;
            return false;
        }

        ReadOnlySpan<byte> extensions = default;
        bool hasExtensions = reader.Remaining > 0;
        if (hasExtensions)
        {
            if (!reader.TryReadVector16(out extensions) || reader.Remaining != 0)
            {
                alert = HandshakeAlert.DecodeError;
                return false;
            }

            if (!ExtensionRules.TryValidate(HandshakeMessageKind.ClientHello, extensions, out alert))
            {
                return false;
            }
        }

        view = new ClientHelloView(version, random, sessionId, ciphers, compression, extensions, hasExtensions);
        return true;
    }

    internal static bool TryReadServerHello(ReadOnlySpan<byte> message, out ServerHelloView view, out HandshakeAlert alert)
    {
        view = default;
        if (!TryBody(message, HandshakeType.ServerHello, out ReadOnlySpan<byte> body, out alert))
        {
            return false;
        }

        TlsReader reader = new(body);
        if (!reader.TryReadUInt16(out ushort version) ||
            !reader.TryReadBytes(32, out ReadOnlySpan<byte> random) ||
            !reader.TryReadVector8(out ReadOnlySpan<byte> sessionId) ||
            !reader.TryReadUInt16(out ushort cipher) ||
            !reader.TryReadUInt8(out byte compression) ||
            !reader.TryReadVector16(out ReadOnlySpan<byte> extensions) ||
            reader.Remaining != 0)
        {
            alert = HandshakeAlert.DecodeError;
            return false;
        }

        if (sessionId.Length > 32 || compression != 0)
        {
            alert = HandshakeAlert.IllegalParameter;
            return false;
        }

        bool helloRetry = random.SequenceEqual(HelloRetryRequestRandom);
        HandshakeMessageKind kind = helloRetry ? HandshakeMessageKind.HelloRetryRequest : HandshakeMessageKind.ServerHello;
        if (!ExtensionRules.TryValidate(kind, extensions, out alert))
        {
            return false;
        }

        view = new ServerHelloView(version, random, sessionId, cipher, compression, extensions, helloRetry);
        return true;
    }

    internal static bool TryReadEncryptedExtensions(ReadOnlySpan<byte> message, out ReadOnlySpan<byte> extensions, out HandshakeAlert alert)
    {
        extensions = default;
        if (!TryBody(message, HandshakeType.EncryptedExtensions, out ReadOnlySpan<byte> body, out alert))
        {
            return false;
        }

        TlsReader reader = new(body);
        if (!reader.TryReadVector16(out extensions) || reader.Remaining != 0)
        {
            alert = HandshakeAlert.DecodeError;
            return false;
        }

        return ExtensionRules.TryValidate(HandshakeMessageKind.EncryptedExtensions, extensions, out alert);
    }

    internal static bool TryWriteEncryptedExtensions(Span<byte> destination, ReadOnlySpan<byte> extensions, out int written)
    {
        written = 0;
        TlsWriter writer = TlsWriter.Over(destination);
        if (!writer.TryWriteUInt8((byte)HandshakeType.EncryptedExtensions) || !writer.TryReserveLength(3, out int lengthAt))
        {
            return false;
        }

        if (!writer.TryWriteVector16(extensions) || !writer.TryPatchLength(lengthAt, 3))
        {
            return false;
        }

        written = writer.Written;
        return true;
    }

    internal static bool TryReadCertificate(ReadOnlySpan<byte> message, out CertificateView view, out HandshakeAlert alert)
    {
        view = default;
        if (!TryBody(message, HandshakeType.Certificate, out ReadOnlySpan<byte> body, out alert))
        {
            return false;
        }

        TlsReader reader = new(body);
        if (!reader.TryReadVector8(out ReadOnlySpan<byte> context) || !reader.TryReadVector24(out ReadOnlySpan<byte> list) || reader.Remaining != 0)
        {
            alert = HandshakeAlert.DecodeError;
            return false;
        }

        if (!TryValidateCertificateList(list, out alert))
        {
            return false;
        }

        view = new CertificateView(context, list);
        return true;
    }

    internal static bool TryReadCertificateRequest(ReadOnlySpan<byte> message, out ReadOnlySpan<byte> context, out ReadOnlySpan<byte> extensions, out HandshakeAlert alert)
    {
        context = default;
        extensions = default;
        if (!TryBody(message, HandshakeType.CertificateRequest, out ReadOnlySpan<byte> body, out alert))
        {
            return false;
        }

        TlsReader reader = new(body);
        if (!reader.TryReadVector8(out context) || !reader.TryReadVector16(out extensions) || reader.Remaining != 0 || extensions.Length < 2)
        {
            alert = HandshakeAlert.DecodeError;
            return false;
        }

        return ExtensionRules.TryValidate(HandshakeMessageKind.CertificateRequest, extensions, out alert);
    }

    internal static bool TryReadCertificateVerify(ReadOnlySpan<byte> message, out ushort scheme, out ReadOnlySpan<byte> signature, out HandshakeAlert alert)
    {
        scheme = 0;
        signature = default;
        if (!TryBody(message, HandshakeType.CertificateVerify, out ReadOnlySpan<byte> body, out alert))
        {
            return false;
        }

        TlsReader reader = new(body);
        if (!reader.TryReadUInt16(out scheme) || !reader.TryReadVector16(out signature) || reader.Remaining != 0)
        {
            alert = HandshakeAlert.DecodeError;
            return false;
        }

        return true;
    }

    internal static bool TryReadFinished(ReadOnlySpan<byte> message, out ReadOnlySpan<byte> verifyData, out HandshakeAlert alert)
    {
        verifyData = default;
        if (!TryBody(message, HandshakeType.Finished, out ReadOnlySpan<byte> body, out alert))
        {
            return false;
        }

        if (body.Length is not (32 or 48))
        {
            alert = HandshakeAlert.DecodeError;
            return false;
        }

        verifyData = body;
        return true;
    }

    internal static bool TryReadNewSessionTicket(
        ReadOnlySpan<byte> message,
        out uint lifetime,
        out uint ageAdd,
        out ReadOnlySpan<byte> nonce,
        out ReadOnlySpan<byte> ticket,
        out ReadOnlySpan<byte> extensions,
        out HandshakeAlert alert)
    {
        lifetime = 0;
        ageAdd = 0;
        nonce = default;
        ticket = default;
        extensions = default;
        if (!TryBody(message, HandshakeType.NewSessionTicket, out ReadOnlySpan<byte> body, out alert))
        {
            return false;
        }

        TlsReader reader = new(body);
        if (!reader.TryReadUInt32(out lifetime) ||
            !reader.TryReadUInt32(out ageAdd) ||
            !reader.TryReadVector8(out nonce) ||
            !reader.TryReadVector16(out ticket) ||
            !reader.TryReadVector16(out extensions) ||
            reader.Remaining != 0 ||
            ticket.IsEmpty)
        {
            alert = HandshakeAlert.DecodeError;
            return false;
        }

        return ExtensionRules.TryValidate(HandshakeMessageKind.NewSessionTicket, extensions, out alert);
    }

    internal static bool TryReadKeyUpdate(ReadOnlySpan<byte> message, out byte request, out HandshakeAlert alert)
    {
        request = 0;
        if (!TryBody(message, HandshakeType.KeyUpdate, out ReadOnlySpan<byte> body, out alert))
        {
            return false;
        }

        if (body.Length != 1 || body[0] > 1)
        {
            alert = HandshakeAlert.IllegalParameter;
            return false;
        }

        request = body[0];
        return true;
    }

    internal static bool TryReadEndOfEarlyData(ReadOnlySpan<byte> message, out HandshakeAlert alert)
    {
        if (!TryBody(message, HandshakeType.EndOfEarlyData, out ReadOnlySpan<byte> body, out alert))
        {
            return false;
        }

        if (!body.IsEmpty)
        {
            alert = HandshakeAlert.DecodeError;
            return false;
        }

        return true;
    }

    internal static bool TryReadCompressedCertificate(
        ReadOnlySpan<byte> message,
        out ushort algorithm,
        out int uncompressedLength,
        out ReadOnlySpan<byte> compressed,
        out HandshakeAlert alert)
    {
        algorithm = 0;
        uncompressedLength = 0;
        compressed = default;
        if (!TryBody(message, HandshakeType.CompressedCertificate, out ReadOnlySpan<byte> body, out alert))
        {
            return false;
        }

        TlsReader reader = new(body);
        if (!reader.TryReadUInt16(out algorithm) ||
            !reader.TryReadUInt24(out uncompressedLength) ||
            !reader.TryReadVector24(out compressed) ||
            reader.Remaining != 0 ||
            uncompressedLength == 0 ||
            compressed.IsEmpty)
        {
            alert = HandshakeAlert.DecodeError;
            return false;
        }

        return true;
    }

    internal static bool TryReadMessageHash(ReadOnlySpan<byte> message, out ReadOnlySpan<byte> hash, out HandshakeAlert alert)
    {
        hash = default;
        if (!TryBody(message, HandshakeType.MessageHash, out ReadOnlySpan<byte> body, out alert))
        {
            return false;
        }

        if (body.Length is not (32 or 48))
        {
            alert = HandshakeAlert.DecodeError;
            return false;
        }

        hash = body;
        return true;
    }

    internal static bool TryReadHostName(ReadOnlySpan<byte> serverNameExtension, out ReadOnlySpan<byte> host)
    {
        host = default;
        TlsReader reader = new(serverNameExtension);
        if (!reader.TryReadVector16(out ReadOnlySpan<byte> list) || reader.Remaining != 0)
        {
            return false;
        }

        TlsReader names = new(list);
        if (!names.TryReadUInt8(out byte nameType) || nameType != 0 || !names.TryReadVector16(out host) || names.Remaining != 0 || host.IsEmpty)
        {
            host = default;
            return false;
        }

        return true;
    }

    private static bool TryValidateCertificateList(ReadOnlySpan<byte> list, out HandshakeAlert alert)
    {
        alert = default;
        TlsReader reader = new(list);
        if (reader.Remaining == 0)
        {
            return true;
        }

        int count = 0;
        while (reader.Remaining > 0)
        {
            count++;
            if (count > MaxCertificateEntries)
            {
                alert = HandshakeAlert.DecodeError;
                return false;
            }

            if (!reader.TryReadVector24(out ReadOnlySpan<byte> certificate) || certificate.IsEmpty || !reader.TryReadVector16(out ReadOnlySpan<byte> extensions))
            {
                alert = HandshakeAlert.DecodeError;
                return false;
            }

            if (!ExtensionRules.TryValidate(HandshakeMessageKind.Certificate, extensions, out alert))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryBody(ReadOnlySpan<byte> message, HandshakeType expected, out ReadOnlySpan<byte> body, out HandshakeAlert alert)
    {
        body = default;
        if (!HandshakeFraming.TrySplitMessage(message, out HandshakeType type, out body, out alert))
        {
            return false;
        }

        if (type != expected)
        {
            alert = HandshakeAlert.UnexpectedMessage;
            return false;
        }

        if (body.Length > HandshakeFraming.HardMaxMessageBytes)
        {
            alert = HandshakeAlert.DecodeError;
            return false;
        }

        return true;
    }
}

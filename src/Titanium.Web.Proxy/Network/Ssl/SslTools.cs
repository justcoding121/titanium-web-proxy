using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.StreamExtended.BufferPool;
using Titanium.Web.Proxy.StreamExtended.Models;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.StreamExtended;

/// <summary>
///     Use this class to peek SSL client/server hello information.
/// </summary>
internal static class SslTools
{
    /// <summary>
    ///     Peek the SSL client hello information.
    /// </summary>
    /// <param name="clientStream"></param>
    /// <param name="bufferPool"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static async Task<ClientHelloInfo?> PeekClientHello(IPeekStream clientStream, IBufferPool bufferPool, // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
        CancellationToken cancellationToken = default)
    {
        // detects the HTTPS ClientHello message as it is described in the following url:
        // https://stackoverflow.com/questions/3897883/how-to-detect-an-incoming-ssl-https-handshake-ssl-wire-format

        var recordType = await clientStream.PeekByteAsync(0, cancellationToken);
        if (recordType == -1) return null;

        if ((recordType & 0x80) == 0x80)
        {
            // SSL 2
            var peekStream = new PeekStreamReader(clientStream, 1);

            // length value + minimum length
            if (!await peekStream.EnsureBufferLength(10, cancellationToken)) return null;

            var recordLength = ((recordType & 0x7f) << 8) + peekStream.ReadByte();
            if (recordLength < 9)
                // Message body too short.
                return null;

            if (peekStream.ReadByte() != 0x01)
                // should be ClientHello
                return null;

            var majorVersion = peekStream.ReadByte();
            var minorVersion = peekStream.ReadByte();

            var cipherSpecsLength = peekStream.ReadInt16();
            var sessionIdLength = peekStream.ReadInt16();
            var randomLength = peekStream.ReadInt16();

            if (cipherSpecsLength % 3 != 0)
                return null;

            var payloadLength = cipherSpecsLength + sessionIdLength + randomLength;
            if (payloadLength > 0 &&
                !await peekStream.EnsureBufferLength(payloadLength, cancellationToken))
                return null;

            var ciphersCount = cipherSpecsLength / 3;
            var ciphers = new int[ciphersCount];
            for (var i = 0; i < ciphers.Length; i++)
                ciphers[i] = (peekStream.ReadByte() << 16) + (peekStream.ReadByte() << 8) + peekStream.ReadByte();

            var sessionId = sessionIdLength > 0 ? peekStream.ReadBytes(sessionIdLength) : Array.Empty<byte>();
            var random = randomLength > 0 ? peekStream.ReadBytes(randomLength) : Array.Empty<byte>();

            var clientHelloInfo = new ClientHelloInfo(2, majorVersion, minorVersion, random, sessionId, ciphers,
                peekStream.Position);

            return clientHelloInfo;
        }

        if (recordType == 0x16)
        {
            var peekStream = new PeekStreamReader(clientStream, 1);

            // should contain at least 43 bytes
            // 2 version + 2 length + 1 type + 3 length(?) + 2 version +  32 random + 1 sessionid length
            if (!await peekStream.EnsureBufferLength(43, cancellationToken)) return null;

            // SSL 3.0 or TLS 1.0, 1.1 and 1.2
            _ = peekStream.ReadByte();
            _ = peekStream.ReadByte();

            var recordLength = peekStream.ReadInt16();

            if (peekStream.ReadByte() != 0x01)
                // should be ClientHello
                return null;

            _ = peekStream.ReadInt24();

            var majorVersion = peekStream.ReadByte();
            var minorVersion = peekStream.ReadByte();

            var random = peekStream.ReadBytes(32);
            var length = peekStream.ReadByte();

            // sessionid + 2 ciphersData length
            if (!await peekStream.EnsureBufferLength(length + 2, cancellationToken)) return null;

            var sessionId = peekStream.ReadBytes(length);

            var ciphersLength = peekStream.ReadInt16();
            if ((ciphersLength & 1) != 0)
                return null;

            // ciphersData + compressionData length
            if (!await peekStream.EnsureBufferLength(ciphersLength + 1, cancellationToken)) return null;

            var ciphers = new int[ciphersLength / 2];
            for (var i = 0; i < ciphers.Length; i++) ciphers[i] = peekStream.ReadInt16();

            length = peekStream.ReadByte();
            if (length < 1) return null;

            // compressionData
            if (!await peekStream.EnsureBufferLength(length, cancellationToken)) return null;

            var compressionData = peekStream.ReadBytes(length);

            var extensionsStartPosition = peekStream.Position;

            Dictionary<string, SslExtension>? extensions = null;

            if (extensionsStartPosition < recordLength + 5)
                extensions = await ReadExtensions(majorVersion, minorVersion, peekStream, cancellationToken);

            var clientHelloInfo = new ClientHelloInfo(3, majorVersion, minorVersion, random, sessionId, ciphers,
                peekStream.Position)
            {
                ExtensionsStartPosition = extensionsStartPosition,
                CompressionData = compressionData,
                Extensions = extensions
            };

            return clientHelloInfo;
        }

        return null;
    }


    /// <summary>
    ///     Is the given stream starts with an SSL server hello?
    /// </summary>
    /// <param name="stream"></param>
    /// <param name="bufferPool"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static async Task<bool> IsServerHello(IPeekStream stream, IBufferPool bufferPool,
        CancellationToken cancellationToken)
    {
        var serverHello = await PeekServerHello(stream, bufferPool, cancellationToken);
        return serverHello != null;
    }

    /// <summary>
    ///     Peek the SSL server hello information.
    /// </summary>
    /// <param name="serverStream"></param>
    /// <param name="bufferPool"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static async Task<ServerHelloInfo?> PeekServerHello(IPeekStream serverStream, IBufferPool bufferPool, // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
        CancellationToken cancellationToken = default)
    {
        // detects the HTTPS ServerHello message as it is described in the following url:
        // https://stackoverflow.com/questions/3897883/how-to-detect-an-incoming-ssl-https-handshake-ssl-wire-format

        var recordType = await serverStream.PeekByteAsync(0, cancellationToken);
        if (recordType == -1) return null;

        if ((recordType & 0x80) == 0x80)
        {
            // SSL 2 SERVER-HELLO layout (deprecated):
            // SESSION-ID-HIT, CERTIFICATE-TYPE, VERSION(2),
            // CERTIFICATE-LENGTH(2), CIPHER-SPECS-LENGTH(2), CONNECTION-ID-LENGTH(2),
            // then certificate, 3-byte cipher-specs, connection-id.
            var peekStream = new PeekStreamReader(serverStream, 1);

            // length byte + msg type + 10-byte fixed header
            if (!await peekStream.EnsureBufferLength(12, cancellationToken)) return null;

            var recordLength = ((recordType & 0x7f) << 8) + peekStream.ReadByte();
            if (recordLength < 11)
                // Message body too short.
                return null;

            if (peekStream.ReadByte() != 0x04)
                // should be ServerHello
                return null;

            _ = peekStream.ReadByte(); // SESSION-ID-HIT
            _ = peekStream.ReadByte(); // CERTIFICATE-TYPE
            var majorVersion = peekStream.ReadByte();
            var minorVersion = peekStream.ReadByte();

            var certificateLength = peekStream.ReadInt16();
            var cipherSpecsLength = peekStream.ReadInt16();
            var connectionIdLength = peekStream.ReadInt16();

            if (cipherSpecsLength % 3 != 0)
                return null;

            var payloadLength = certificateLength + cipherSpecsLength + connectionIdLength;
            if (payloadLength > 0 &&
                !await peekStream.EnsureBufferLength(payloadLength, cancellationToken))
                return null;

            if (certificateLength > 0)
                _ = peekStream.ReadBytes(certificateLength);

            var cipherSuite = 0;
            if (cipherSpecsLength >= 3)
            {
                cipherSuite = (peekStream.ReadByte() << 16) + (peekStream.ReadByte() << 8) + peekStream.ReadByte();
                if (cipherSpecsLength > 3)
                    _ = peekStream.ReadBytes(cipherSpecsLength - 3);
            }

            var sessionId = connectionIdLength > 0
                ? peekStream.ReadBytes(connectionIdLength)
                : Array.Empty<byte>();

            // SSL 2 has no TLS-style 32-byte random field.
            var serverHelloInfo = new ServerHelloInfo(2, majorVersion, minorVersion, Array.Empty<byte>(), sessionId,
                cipherSuite, peekStream.Position);

            return serverHelloInfo;
        }

        if (recordType == 0x16)
        {
            var peekStream = new PeekStreamReader(serverStream, 1);

            // should contain at least 43 bytes
            // 2 version + 2 length + 1 type + 3 length(?) + 2 version +  32 random + 1 sessionid length
            if (!await peekStream.EnsureBufferLength(43, cancellationToken)) return null;

            // SSL 3.0 or TLS 1.0, 1.1 and 1.2
            _ = peekStream.ReadByte();
            _ = peekStream.ReadByte();

            var recordLength = peekStream.ReadInt16();

            if (peekStream.ReadByte() != 0x02)
                // should be ServerHello
                return null;

            _ = peekStream.ReadInt24();

            var majorVersion = peekStream.ReadByte();
            var minorVersion = peekStream.ReadByte();

            var random = peekStream.ReadBytes(32);
            var length = peekStream.ReadByte();

            // sessionid + cipherSuite + compressionMethod
            if (!await peekStream.EnsureBufferLength(length + 2 + 1, cancellationToken)) return null;

            var sessionId = peekStream.ReadBytes(length);

            var cipherSuite = peekStream.ReadInt16();
            var compressionMethod = peekStream.ReadByte();

            var extensionsStartPosition = peekStream.Position;

            Dictionary<string, SslExtension>? extensions = null;

            if (extensionsStartPosition < recordLength + 5)
                extensions = await ReadExtensions(majorVersion, minorVersion, peekStream, cancellationToken);

            var serverHelloInfo = new ServerHelloInfo(3, majorVersion, minorVersion, random, sessionId, cipherSuite,
                peekStream.Position)
            {
                CompressionMethod = compressionMethod,
                ExtensionsStartPosition = extensionsStartPosition,
                Extensions = extensions
            };

            return serverHelloInfo;
        }

        return null;
    }

    private static async Task<Dictionary<string, SslExtension>?> ReadExtensions(int majorVersion, int minorVersion,
        PeekStreamReader peekStreamReader, CancellationToken cancellationToken)
    {
        Dictionary<string, SslExtension>? extensions = null;
        if ((majorVersion > 3 || majorVersion == 3 && minorVersion >= 1) &&
            await peekStreamReader.EnsureBufferLength(2, cancellationToken))
        {
            var extensionsLength = peekStreamReader.ReadInt16();

            if (extensionsLength > 0 &&
                await peekStreamReader.EnsureBufferLength(extensionsLength, cancellationToken))
            {
                var extensionsData = peekStreamReader.ReadBytes(extensionsLength).AsMemory();
                extensions = new Dictionary<string, SslExtension>();
                var idx = 0;
                while (extensionsData.Length >= 4)
                {
                    var id = BinaryPrimitives.ReadUInt16BigEndian(extensionsData.Span);
                    var length = BinaryPrimitives.ReadUInt16BigEndian(extensionsData.Span.Slice(2));
                    if (extensionsData.Length < 4 + length)
                        // Truncated or oversize extension — keep extensions parsed so far.
                        break;

                    var extension = new SslExtension(id, extensionsData.Slice(4, length), idx++);
                    extensions[extension.Name] = extension;
                    extensionsData = extensionsData.Slice(4 + length);
                }
            }
        }

        return extensions;
    }
}

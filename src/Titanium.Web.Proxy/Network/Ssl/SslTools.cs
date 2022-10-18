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
internal class SslTools
{
    /// <summary>
    ///     Peek the SSL client hello information.
    /// </summary>
    /// <param name="clientStream"></param>
    /// <param name="bufferPool"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static async Task<ClientHelloInfo?> PeekClientHello(IPeekStream clientStream, IBufferPool bufferPool,
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

            int majorVersion = peekStream.ReadByte();
            int minorVersion = peekStream.ReadByte();

            var ciphersCount = peekStream.ReadInt16() / 3;
            var sessionIdLength = peekStream.ReadInt16();
            var randomLength = peekStream.ReadInt16();

            if (!await peekStream.EnsureBufferLength(ciphersCount * 3 + sessionIdLength + randomLength,
                    cancellationToken)) return null;

            var ciphers = new int[ciphersCount];
            for (var i = 0; i < ciphers.Length; i++)
                ciphers[i] = (peekStream.ReadByte() << 16) + (peekStream.ReadByte() << 8) + peekStream.ReadByte();

            var sessionId = peekStream.ReadBytes(sessionIdLength);
            var random = peekStream.ReadBytes(randomLength);

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
            int majorVersion = peekStream.ReadByte();
            int minorVersion = peekStream.ReadByte();

            var recordLength = peekStream.ReadInt16();

            if (peekStream.ReadByte() != 0x01)
                // should be ClientHello
                return null;

            var length = peekStream.ReadInt24();

            majorVersion = peekStream.ReadByte();
            minorVersion = peekStream.ReadByte();

            var random = peekStream.ReadBytes(32);
            length = peekStream.ReadByte();

            // sessionid + 2 ciphersData length
            if (!await peekStream.EnsureBufferLength(length + 2, cancellationToken)) return null;

            var sessionId = peekStream.ReadBytes(length);

            length = peekStream.ReadInt16();

            // ciphersData + compressionData length
            if (!await peekStream.EnsureBufferLength(length + 1, cancellationToken)) return null;

            var ciphers = new int[length / 2];
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
    ///     Is the given stream starts with an SSL client hello?
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
    ///     Peek the SSL client hello information.
    /// </summary>
    /// <param name="serverStream"></param>
    /// <param name="bufferPool"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static async Task<ServerHelloInfo?> PeekServerHello(IPeekStream serverStream, IBufferPool bufferPool,
        CancellationToken cancellationToken = default)
    {
        // detects the HTTPS ClientHello message as it is described in the following url:
        // https://stackoverflow.com/questions/3897883/how-to-detect-an-incoming-ssl-https-handshake-ssl-wire-format

        var recordType = await serverStream.PeekByteAsync(0, cancellationToken);
        if (recordType == -1) return null;

        if ((recordType & 0x80) == 0x80)
        {
            // SSL 2
            // not tested. SSL2 is deprecated
            var peekStream = new PeekStreamReader(serverStream, 1);

            // length value + minimum length
            if (!await peekStream.EnsureBufferLength(39, cancellationToken)) return null;

            var recordLength = ((recordType & 0x7f) << 8) + peekStream.ReadByte();
            if (recordLength < 38)
                // Message body too short.
                return null;

            if (peekStream.ReadByte() != 0x04)
                // should be ServerHello
                return null;

            int majorVersion = peekStream.ReadByte();
            int minorVersion = peekStream.ReadByte();

            // 32 bytes random + 1 byte sessionId + 2 bytes cipherSuite
            if (!await peekStream.EnsureBufferLength(35, cancellationToken)) return null;

            var random = peekStream.ReadBytes(32);
            var sessionId = peekStream.ReadBytes(1);
            var cipherSuite = peekStream.ReadInt16();

            var serverHelloInfo = new ServerHelloInfo(2, majorVersion, minorVersion, random, sessionId, cipherSuite,
                peekStream.Position);

            return serverHelloInfo;
        }

        if (recordType == 0x16)
        {
            var peekStream = new PeekStreamReader(serverStream, 1);

            // should contain at least 43 bytes
            // 2 version + 2 length + 1 type + 3 length(?) + 2 version +  32 random + 1 sessionid length
            if (!await peekStream.EnsureBufferLength(43, cancellationToken)) return null;

            // SSL 3.0 or TLS 1.0, 1.1 and 1.2
            int majorVersion = peekStream.ReadByte();
            int minorVersion = peekStream.ReadByte();

            var recordLength = peekStream.ReadInt16();

            if (peekStream.ReadByte() != 0x02)
                // should be ServerHello
                return null;

            var length = peekStream.ReadInt24();

            majorVersion = peekStream.ReadByte();
            minorVersion = peekStream.ReadByte();

            var random = peekStream.ReadBytes(32);
            length = peekStream.ReadByte();

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
                EntensionsStartPosition = extensionsStartPosition,
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
        if (majorVersion > 3 || majorVersion == 3 && minorVersion >= 1)
            if (await peekStreamReader.EnsureBufferLength(2, cancellationToken))
            {
                var extensionsLength = peekStreamReader.ReadInt16();

                if (await peekStreamReader.EnsureBufferLength(extensionsLength, cancellationToken))
                {
                    extensions = new Dictionary<string, SslExtension>();
                    var idx = 0;
                    while (extensionsLength > 3)
                    {
                        var id = peekStreamReader.ReadInt16();
                        var length = peekStreamReader.ReadInt16();
                        var data = peekStreamReader.ReadBytes(length);
                        var extension = SslExtensions.GetExtension(id, data, idx++);
                        extensions[extension.Name] = extension;
                        extensionsLength -= 4 + length;
                    }
                }
            }

        return extensions;
    }
}
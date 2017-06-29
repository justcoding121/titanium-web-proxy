using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Helpers;

namespace Titanium.Web.Proxy.Ssl
{
    class SslTools
    {
        public static async Task<bool> IsClientHello(CustomBufferedStream clientStream)
        {
            var clientHello = await GetClientHelloInfo(clientStream);
            return clientHello != null;
        }

        public static async Task<ClientHelloInfo> GetClientHelloInfo(CustomBufferedStream clientStream)
        {
            //detects the HTTPS ClientHello message as it is described in the following url:
            //https://stackoverflow.com/questions/3897883/how-to-detect-an-incoming-ssl-https-handshake-ssl-wire-format

            int recordType = await clientStream.PeekByteAsync(0);
            if (recordType == 0x80)
            {
                var peekStream = new CustomBufferedPeekStream(clientStream, 1);

                //SSL 2
                int length = peekStream.ReadByte();
                if (length < 9)
                {
                    // Message body too short.
                    return null;
                }

                if (peekStream.ReadByte() != 0x01)
                {
                    // should be ClientHello
                    return null;
                }

                int majorVersion = clientStream.ReadByte();
                int minorVersion = clientStream.ReadByte();
                return new ClientHelloInfo();
            }
            else if (recordType == 0x16)
            {
                var peekStream = new CustomBufferedPeekStream(clientStream, 1);

                //should contain at least 43 bytes
                // 2 version + 2 length + 1 type + 3 length(?) + 2 version +  32 random + 1 sessionid length
                if (!await peekStream.EnsureBufferLength(43))
                {
                    return null;
                }

                //SSL 3.0 or TLS 1.0, 1.1 and 1.2
                int majorVersion = peekStream.ReadByte();
                int minorVersion = peekStream.ReadByte();

                int length = peekStream.ReadInt16();

                if (peekStream.ReadByte() != 0x01)
                {
                    // should be ClientHello
                    return null;
                }

                length = peekStream.ReadInt24();

                majorVersion = peekStream.ReadByte();
                minorVersion = peekStream.ReadByte();

                byte[] random = peekStream.ReadBytes(32);
                length = peekStream.ReadByte();

                // sessionid + 2 ciphersData length
                if (!await peekStream.EnsureBufferLength(length + 2))
                {
                    return null;
                }

                byte[] sessionId = peekStream.ReadBytes(length);

                length = peekStream.ReadInt16();

                // ciphersData + compressionData length
                if (!await peekStream.EnsureBufferLength(length + 1))
                {
                    return null;
                }

                byte[] ciphersData = peekStream.ReadBytes(length);
                int[] ciphers = new int[ciphersData.Length / 2];
                for (int i = 0; i < ciphers.Length; i++)
                {
                    ciphers[i] = (ciphersData[2 * i] << 8) + ciphersData[2 * i + 1];
                }

                length = peekStream.ReadByte();
                if (length < 1)
                {
                    return null;
                }

                // compressionData
                if (!await peekStream.EnsureBufferLength(length))
                {
                    return null;
                }

                byte[] compressionData = peekStream.ReadBytes(length);

                int extenstionsStartPosition = peekStream.Position;

                var extensions = await ReadExtensions(majorVersion, minorVersion, peekStream);

                //var rawBytes = new CustomBufferedPeekStream(clientStream).ReadBytes(peekStream.Position);

                var clientHelloInfo = new ClientHelloInfo
                {
                    MajorVersion = majorVersion,
                    MinorVersion = minorVersion,
                    Random = random,
                    SessionId = sessionId,
                    Ciphers = ciphers,
                    CompressionData = compressionData,
                    ClientHelloLength = peekStream.Position,
                    EntensionsStartPosition = extenstionsStartPosition,
                    Extensions = extensions,
                };

                return clientHelloInfo;
            }

            return null;
        }

        private static async Task<List<SslExtension>> ReadExtensions(int majorVersion, int minorVersion, CustomBufferedPeekStream peekStream)
        {
            List<SslExtension> extensions = null;
            if (majorVersion > 3 || majorVersion == 3 && minorVersion >= 1)
            {
                if (await peekStream.EnsureBufferLength(2))
                {
                    int extensionsLength = peekStream.ReadInt16();

                    if (await peekStream.EnsureBufferLength(extensionsLength))
                    {
                        extensions = new List<SslExtension>();
                        while (extensionsLength > 3)
                        {
                            int id = peekStream.ReadInt16();
                            int length = peekStream.ReadInt16();
                            byte[] data = peekStream.ReadBytes(length);
                            extensions.Add(SslExtensions.GetExtension(id, data));
                            extensionsLength -= 4 + length;
                        }
                    }
                }
            }

            return extensions;
        }

        public static async Task<bool> IsServerHello(CustomBufferedStream serverStream)
        {
            var serverHello = await GetServerHelloInfo(serverStream);
            return serverHello != null;
        }

        public static async Task<ServerHelloInfo> GetServerHelloInfo(CustomBufferedStream serverStream)
        {
            //detects the HTTPS ClientHello message as it is described in the following url:
            //https://stackoverflow.com/questions/3897883/how-to-detect-an-incoming-ssl-https-handshake-ssl-wire-format

            int recordType = await serverStream.PeekByteAsync(0);
            if (recordType == 0x80)
            {
                // copied from client hello, not tested. SSL2 is deprecated
                var peekStream = new CustomBufferedPeekStream(serverStream, 1);

                //SSL 2
                int length = peekStream.ReadByte();
                if (length < 9)
                {
                    // Message body too short.
                    return null;
                }

                if (peekStream.ReadByte() != 0x02)
                {
                    // should be ServerHello
                    return null;
                }

                int majorVersion = serverStream.ReadByte();
                int minorVersion = serverStream.ReadByte();
                return new ServerHelloInfo();
            }
            else if (recordType == 0x16)
            {
                var peekStream = new CustomBufferedPeekStream(serverStream, 1);

                //should contain at least 43 bytes
                // 2 version + 2 length + 1 type + 3 length(?) + 2 version +  32 random + 1 sessionid length
                if (!await peekStream.EnsureBufferLength(43))
                {
                    return null;
                }

                //SSL 3.0 or TLS 1.0, 1.1 and 1.2
                int majorVersion = peekStream.ReadByte();
                int minorVersion = peekStream.ReadByte();

                int length = peekStream.ReadInt16();

                if (peekStream.ReadByte() != 0x02)
                {
                    // should be ServerHello
                    return null;
                }

                length = peekStream.ReadInt24();

                majorVersion = peekStream.ReadByte();
                minorVersion = peekStream.ReadByte();

                byte[] random = peekStream.ReadBytes(32);
                length = peekStream.ReadByte();

                // sessionid + cipherSuite + compressionMethod
                if (!await peekStream.EnsureBufferLength(length + 2 + 1))
                {
                    return null;
                }

                byte[] sessionId = peekStream.ReadBytes(length);

                int cipherSuite = peekStream.ReadInt16();
                byte compressionMethod = peekStream.ReadByte();

                var extensions = await ReadExtensions(majorVersion, minorVersion, peekStream);

                var serverHelloInfo = new ServerHelloInfo
                {
                    MajorVersion = majorVersion,
                    MinorVersion = minorVersion,
                    Random = random,
                    SessionId = sessionId,
                    CipherSuite = cipherSuite,
                    CompressionMethod = compressionMethod,
                    Extensions = extensions,
                };

                return serverHelloInfo;
            }

            return null;
        }
    }
}

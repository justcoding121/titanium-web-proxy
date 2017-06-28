using System.Collections.Generic;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Ssl
{
    class HttpsTools
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

                List<SslExtension> extensions = null;
                if (majorVersion > 3 || majorVersion == 3 && minorVersion >= 1)
                {
                    if (await peekStream.EnsureBufferLength(2))
                    {
                        length = peekStream.ReadInt16();

                        if (await peekStream.EnsureBufferLength(length))
                        {
                            extensions = new List<SslExtension>();
                            while (peekStream.Available > 3)
                            {
                                int id = peekStream.ReadInt16();
                                length = peekStream.ReadInt16();
                                byte[] data = peekStream.ReadBytes(length);
                                extensions.Add(SslExtensions.GetExtension(id, data));
                            }
                        }
                    }
                }

                var clientHelloInfo = new ClientHelloInfo
                {
                    MajorVersion = majorVersion,
                    MinorVersion = minorVersion,
                    Random = random,
                    SessionId = sessionId,
                    Ciphers = ciphers,
                    CompressionData = compressionData,
                    Extensions = extensions,
                };

                return clientHelloInfo;
            }

            return null;
        }
    }
}

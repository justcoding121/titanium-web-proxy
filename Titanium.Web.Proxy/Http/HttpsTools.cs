using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;

namespace Titanium.Web.Proxy.Http
{
    class HttpsTools
    {
        public static async Task<bool> IsClientHello(CustomBufferedStream clientStream, TunnelConnectSessionEventArgs connectArgs)
        {
            //detects the HTTPS ClientHello message as it is described in the following url:
            //https://stackoverflow.com/questions/3897883/how-to-detect-an-incoming-ssl-https-handshake-ssl-wire-format

            var request = (ConnectRequest)connectArgs.WebSession.Request;

            int recordType = await clientStream.PeekByteAsync(0);
            if (recordType == 0x80)
            {
                var peekStream = new CustomBufferedPeekStream(clientStream, 1);

                //SSL 2
                int length = peekStream.ReadByte();
                if (length < 9)
                {
                    // Message body too short.
                    return false;
                }

                if (peekStream.ReadByte() != 0x01)
                {
                    // should be ClientHello
                    return false;
                }

                int majorVersion = clientStream.ReadByte();
                int minorVersion = clientStream.ReadByte();
                return true;
            }
            else if (recordType == 0x16)
            {
                var peekStream = new CustomBufferedPeekStream(clientStream, 1);

                //should contain at least 43 bytes
                int requiredLength = 43; // 2 version + 2 length + 1 type + 3 length(?) + 2 version +  32 random + 1 sessionid length
                if (!await peekStream.EnsureBufferLength(requiredLength))
                {
                    return false;
                }

                //SSL 3.0 or TLS 1.0, 1.1 and 1.2
                int majorVersion = peekStream.ReadByte();
                int minorVersion = peekStream.ReadByte();

                int length = peekStream.ReadInt16();
                
                if (peekStream.ReadByte() != 0x01)
                {
                    // should be ClientHello
                    return false;
                }

                length = peekStream.ReadInt24();

                majorVersion = peekStream.ReadByte();
                minorVersion = peekStream.ReadByte();

                byte[] random = peekStream.ReadBytes(32);
                length = peekStream.ReadByte();

                requiredLength += length + 2; // sessionid + 2 data length
                if (!await peekStream.EnsureBufferLength(requiredLength))
                {
                    return false;
                }

                byte[] sessionId = peekStream.ReadBytes(length);

                length = peekStream.ReadInt16();

                requiredLength += length + 1; // data + data2 length
                if (!await peekStream.EnsureBufferLength(requiredLength))
                {
                    return false;
                }

                byte[] data = peekStream.ReadBytes(length);

                length = peekStream.ReadByte();
                if (length < 1)
                {
                    return false;
                }

                requiredLength += length; // data2
                if (!await peekStream.EnsureBufferLength(requiredLength))
                {
                    return false;
                }

                byte[] data2 = peekStream.ReadBytes(length);

                byte[] data3 = null;
                if (majorVersion > 3 || majorVersion == 3 && minorVersion >= 1)
                {
                    requiredLength += 2;
                    if (await peekStream.EnsureBufferLength(requiredLength))
                    {
                        length = peekStream.ReadInt16();

                        requiredLength += length;
                        if (await peekStream.EnsureBufferLength(requiredLength))
                        {
                            data3 = peekStream.ReadBytes(length);
                        }
                    }
                }

                request.ClientHelloInfo = new ClientHelloInfo
                {
                    MajorVersion = majorVersion,
                    MinorVersion = minorVersion,
                    Random = random,
                    SessionId = sessionId,
                };

                return true;
            }

            return false;
        }
    }
}

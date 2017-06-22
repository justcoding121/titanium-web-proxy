using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Helpers;

namespace Titanium.Web.Proxy.Http
{
    class HttpsTools
    {
        public static async Task<bool> IsClientHello(CustomBufferedStream clientStream)
        {
            //detects the HTTPS ClientHello message as it is described in the following url:
            //https://stackoverflow.com/questions/3897883/how-to-detect-an-incoming-ssl-https-handshake-ssl-wire-format

            int recordType = await clientStream.PeekByteAsync(0);
            if (recordType == 0x80)
            {
                //SSL 2
                int length = await clientStream.PeekByteAsync(1);
                if (length < 9)
                {
                    // Message body too short.
                    return false;
                }

                if (await clientStream.PeekByteAsync(2) != 0x01)
                {
                    // should be ClientHello
                    return false;
                }

                int majorVersion = await clientStream.PeekByteAsync(3);
                int minorVersion = await clientStream.PeekByteAsync(4);
                return true;
            }
            else if (recordType == 0x16)
            {
                //SSL 3.0 or TLS 1.0, 1.1 and 1.2
                int majorVersion = await clientStream.PeekByteAsync(1);
                int minorVersion = await clientStream.PeekByteAsync(2);

                int length1 = await clientStream.PeekByteAsync(3);
                int length2 = await clientStream.PeekByteAsync(4);
                int length = (length1 << 8) + length2;

                if (await clientStream.PeekByteAsync(5) != 0x01)
                {
                    // should be ClientHello
                    return false;
                }

                return true;
            }

            return false;
        }
    }
}

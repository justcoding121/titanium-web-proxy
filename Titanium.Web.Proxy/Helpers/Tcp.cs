using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net.Security;
using System.IO;
using System.Net;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Helpers
{
    public class TcpHelper
    {
        private static readonly int BUFFER_SIZE = 8192;
        private static readonly String[] colonSpaceSplit = new string[] { ": " };
        public static void SendRaw(string hostname, int tunnelPort, System.IO.Stream clientStream)
        {


            System.Net.Sockets.TcpClient tunnelClient = new System.Net.Sockets.TcpClient(hostname, tunnelPort);
            var tunnelStream = tunnelClient.GetStream();
            var tunnelReadBuffer = new byte[BUFFER_SIZE];

            Task sendRelay = new Task(() => StreamHelper.CopyTo(clientStream, tunnelStream, BUFFER_SIZE));
            Task receiveRelay = new Task(() => StreamHelper.CopyTo(tunnelStream, clientStream, BUFFER_SIZE));

            sendRelay.Start();
            receiveRelay.Start();

            Task.WaitAll(sendRelay, receiveRelay);

            if (tunnelStream != null)
                tunnelStream.Close();

            if (tunnelClient != null)
                tunnelClient.Close();
        }
        public static void SendRaw(string httpCmd, string secureHostName, List<string> requestLines, bool isHttps, Stream clientStream)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(httpCmd);
            sb.Append(Environment.NewLine);

            string hostname = secureHostName;
            for (int i = 1; i < requestLines.Count; i++)
            {
                var header = requestLines[i];


                if (secureHostName == null)
                {
                    String[] headerParsed = httpCmd.Split(colonSpaceSplit, 2, StringSplitOptions.None);
                    switch (headerParsed[0].ToLower())
                    {
                        case "host":
                            var hostdetail = headerParsed[1];
                            if (hostdetail.Contains(":"))
                                hostname = hostdetail.Split(':')[0].Trim();
                            else
                                hostname = hostdetail.Trim();
                            break;
                        default:
                            break;
                    }
                }
                sb.Append(header);
                sb.Append(Environment.NewLine);
            }
            sb.Append(Environment.NewLine);

            int tunnelPort = 80;
            if (isHttps)
            {

                tunnelPort = 443;

            }

            System.Net.Sockets.TcpClient tunnelClient = new System.Net.Sockets.TcpClient(hostname, tunnelPort);
            var tunnelStream = tunnelClient.GetStream() as System.IO.Stream;

            if (isHttps)
            {
                var sslStream = new SslStream(tunnelStream);
                sslStream.AuthenticateAsClient(hostname);
                tunnelStream = sslStream;
            }


            var sendRelay = new Task(() => StreamHelper.CopyTo(sb.ToString(), clientStream, tunnelStream, BUFFER_SIZE));
            var receiveRelay = new Task(() => StreamHelper.CopyTo(tunnelStream, clientStream, BUFFER_SIZE));

            sendRelay.Start();
            receiveRelay.Start();

            Task.WaitAll(sendRelay, receiveRelay);

            if (tunnelStream != null)
                tunnelStream.Close();

            if (tunnelClient != null)
                tunnelClient.Close();
        }
    }
}
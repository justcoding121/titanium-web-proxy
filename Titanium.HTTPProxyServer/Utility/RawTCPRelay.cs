using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net.Security;
using System.IO;
using System.Net;
using System.Threading.Tasks;

namespace Titanium.HTTPProxyServer
{
  public partial class ProxyServer
    {
      public static void sendRaw(string hostname, int tunnelPort, System.IO.Stream clientStream)
        {
           

            System.Net.Sockets.TcpClient tunnelClient = new System.Net.Sockets.TcpClient(hostname, tunnelPort);
            var tunnelStream = tunnelClient.GetStream();
            var tunnelReadBuffer = new byte[BUFFER_SIZE];

            Task sendRelay = new Task(() => StreamUtilities.CopyTo(clientStream, tunnelStream, BUFFER_SIZE));
            Task receiveRelay = new Task(() => StreamUtilities.CopyTo(tunnelStream, clientStream, BUFFER_SIZE));

            sendRelay.Start();
            receiveRelay.Start();

            Task.WaitAll(sendRelay, receiveRelay); 

            if (tunnelStream != null)
                tunnelStream.Close();

            if (tunnelClient != null)
                tunnelClient.Close();
        }
      private static void sendRaw(string httpCmd, string secureHostName, ref List<string> requestLines, bool isSecure, Stream clientStream)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(httpCmd);
            sb.Append(Environment.NewLine);
         
            string hostname= secureHostName;
            for(int i = 1; i < requestLines.Count;i++)
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

            if (hostname == null)
            {
              //  Dns.geth
            }

            int tunnelPort = 80;
            if (isSecure)
            {
             
                tunnelPort = 443;

            }

            System.Net.Sockets.TcpClient tunnelClient = new System.Net.Sockets.TcpClient(hostname, tunnelPort);
            var tunnelStream = tunnelClient.GetStream() as System.IO.Stream;

            if (isSecure)
            {
                var sslStream = new SslStream(tunnelStream);
                sslStream.AuthenticateAsClient(hostname);
                tunnelStream = sslStream;
            }

       
            var sendRelay = new Task(() => StreamUtilities.CopyTo(sb.ToString(), clientStream, tunnelStream, BUFFER_SIZE));
            var receiveRelay = new Task(() => StreamUtilities.CopyTo(tunnelStream, clientStream, BUFFER_SIZE));

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

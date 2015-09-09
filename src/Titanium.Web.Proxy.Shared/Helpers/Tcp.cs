using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net.Security;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using System.Net.Sockets;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Helpers
{
    public class TcpHelper
    {
        private static readonly int BUFFER_SIZE = 8192;
        private static readonly String[] colonSpaceSplit = new string[] { ": " };
      
        public static void SendRaw(Stream clientStream, string httpCmd, List<HttpHeader> requestHeaders, string hostName, int tunnelPort, bool isHttps)
        {
            StringBuilder sb = null;
            if (httpCmd != null || requestHeaders != null)
            {
                sb = new StringBuilder();
                if (httpCmd != null)
                {
                    sb.Append(httpCmd);
                    sb.Append(Environment.NewLine);
                }
                if (requestHeaders != null)
                for (int i = 0; i < requestHeaders.Count; i++)
                {
                    var header = requestHeaders[i].ToString();
                    sb.Append(header);
                    sb.Append(Environment.NewLine);
                }
                sb.Append(Environment.NewLine);
            }
        

            System.Net.Sockets.TcpClient tunnelClient = null;
            Stream tunnelStream = null;
            
            try
            {
                tunnelClient = new System.Net.Sockets.TcpClient(hostName, tunnelPort);
                tunnelStream = tunnelClient.GetStream() as Stream;

                if (isHttps)
                {
                    SslStream sslStream = null;
                    try
                    {
                        sslStream = new SslStream(tunnelStream);
                        sslStream.AuthenticateAsClient(hostName);
                        tunnelStream = sslStream;
                    }
                    catch
                    {
                        if (sslStream != null)
                            sslStream.Dispose();
                    }
                }


                var sendRelay = Task.Factory.StartNew(() => { 
                    if(sb!=null) 
                        clientStream.CopyToAsync(sb.ToString(), tunnelStream, BUFFER_SIZE);
                    else
                        clientStream.CopyToAsync(tunnelStream, BUFFER_SIZE);
                });

                var receiveRelay = Task.Factory.StartNew(() => tunnelStream.CopyToAsync(clientStream, BUFFER_SIZE));

                Task.WaitAll(sendRelay, receiveRelay);
            }
            catch
            {
                if (tunnelStream != null)
                {
                    tunnelStream.Close();
                    tunnelStream.Dispose();
                }

                if (tunnelClient != null)
                    tunnelClient.Close();

                throw;
            }
        }

    
    }
}
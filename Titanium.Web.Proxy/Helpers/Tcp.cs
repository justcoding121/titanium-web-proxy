using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Helpers
{
    public class TcpHelper
    {
        private static readonly int BUFFER_SIZE = 8192;

        public static void SendRaw(Stream clientStream, string httpCmd, List<HttpHeader> requestHeaders, string hostName,
            int tunnelPort, bool isHttps)
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
                    foreach (var header in requestHeaders.Select(t => t.ToString()))
                    {
                        sb.Append(header);
                        sb.Append(Environment.NewLine);
                    }
                sb.Append(Environment.NewLine);
            }


            TcpClient tunnelClient = null;
            Stream tunnelStream = null;

            try
            {
                tunnelClient = new TcpClient(hostName, tunnelPort);
                tunnelStream = tunnelClient.GetStream();

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

                        throw;
                    }
                }


                var sendRelay = Task.Factory.StartNew(() =>
                {
                    if (sb != null)
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
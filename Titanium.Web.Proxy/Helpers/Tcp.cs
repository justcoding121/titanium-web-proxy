using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.Helpers
{

    internal class TcpHelper
    {
        /// <summary>
        /// relays the input clientStream to the server at the specified host name & port with the given httpCmd & headers as prefix
        /// Usefull for websocket requests
        /// </summary>
        /// <param name="clientStream"></param>
        /// <param name="httpCmd"></param>
        /// <param name="requestHeaders"></param>
        /// <param name="hostName"></param>
        /// <param name="tunnelPort"></param>
        /// <param name="isHttps"></param>
        /// <returns></returns>
        internal static async Task SendRaw(TcpConnectionFactory tcpConnectionFactory, Stream clientStream, string httpCmd, Version version,
            Dictionary<string, HttpHeader> requestHeaders, string hostName, int bufferSize,
            int tunnelPort, bool isHttps, SslProtocols supportedProtocols, int connectionTimeOutSeconds,
            RemoteCertificateValidationCallback remoteCertificateValidationCallback, LocalCertificateSelectionCallback localCertificateSelectionCallback)
        {
            //prepare the prefix content
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
                {
                    foreach (var header in requestHeaders.Select(t => t.Value.ToString()))
                    {
                        sb.Append(header);
                        sb.Append(Environment.NewLine);
                    }
                }

                sb.Append(Environment.NewLine);
            }

            var tcpConnection = await tcpConnectionFactory.GetClient(hostName, tunnelPort, isHttps, version, null, null, bufferSize,
                 supportedProtocols, connectionTimeOutSeconds, remoteCertificateValidationCallback, localCertificateSelectionCallback);

            TcpClient tunnelClient = tcpConnection.TcpClient;
            Stream tunnelStream = tcpConnection.Stream;

            try
            {
                Task sendRelay;

                //Now async relay all server=>client & client=>server data
                if (sb != null)
                {
                    sendRelay = clientStream.CopyToAsync(sb.ToString(), tunnelStream);
                }
                else
                {
                    sendRelay = clientStream.CopyToAsync(string.Empty, tunnelStream);
                }


                var receiveRelay = tunnelStream.CopyToAsync(string.Empty, clientStream);

                await Task.WhenAll(sendRelay, receiveRelay);
            }
            catch
            {
                throw;
            }
            finally
            {
                tunnelStream.Dispose();
            }
        }

    }
}
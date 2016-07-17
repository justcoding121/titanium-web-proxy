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
        /// <param name="bufferSize"></param>
        /// <param name="connectionTimeOutSeconds"></param>
        /// <param name="remoteHostName"></param>
        /// <param name="httpCmd"></param>
        /// <param name="httpVersion"></param>
        /// <param name="requestHeaders"></param>
        /// <param name="isHttps"></param>
        /// <param name="remotePort"></param>
        /// <param name="supportedProtocols"></param>
        /// <param name="remoteCertificateValidationCallback"></param>
        /// <param name="localCertificateSelectionCallback"></param>
        /// <param name="clientStream"></param>
        /// <param name="tcpConnectionFactory"></param>
        /// <returns></returns>
        internal static async Task SendRaw(int bufferSize, int connectionTimeOutSeconds,
            string remoteHostName, int remotePort, string httpCmd, Version httpVersion, Dictionary<string, HttpHeader> requestHeaders,
            bool isHttps,  SslProtocols supportedProtocols,
            RemoteCertificateValidationCallback remoteCertificateValidationCallback, LocalCertificateSelectionCallback localCertificateSelectionCallback,
            Stream clientStream, TcpConnectionFactory tcpConnectionFactory)
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

            var tcpConnection = await tcpConnectionFactory.CreateClient(bufferSize, connectionTimeOutSeconds,
                                        remoteHostName, remotePort,
                                        httpVersion, isHttps, 
                                        supportedProtocols, remoteCertificateValidationCallback, localCertificateSelectionCallback, 
                                        null, null, clientStream);
                                                                
            try
            {
                TcpClient tunnelClient = tcpConnection.TcpClient;

                Stream tunnelStream = tcpConnection.Stream;

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
                tcpConnection.Dispose();
            }
        }

    }
}
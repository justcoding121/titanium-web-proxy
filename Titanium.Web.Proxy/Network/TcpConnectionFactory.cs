using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Net.Security;
using Titanium.Web.Proxy.Helpers;
using System.Threading;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Models;
using System.Security.Authentication;

namespace Titanium.Web.Proxy.Network
{
    /// <summary>
    /// A class that manages Tcp Connection to server used by this proxy server
    /// </summary>
    internal class TcpConnectionFactory
    {
        /// <summary>
        /// Get a TcpConnection to the specified host, port optionally HTTPS and a particular HTTP version
        /// </summary>
        /// <param name="hostname"></param>
        /// <param name="port"></param>
        /// <param name="isHttps"></param>
        /// <param name="version"></param>
        /// <returns></returns>
        internal async Task<TcpConnection> GetClient(string hostname, int port, bool isHttps, Version version,
            ExternalProxy upStreamHttpProxy, ExternalProxy upStreamHttpsProxy, int bufferSize, SslProtocols supportedSslProtocols,
            int connectionTimeOutSeconds,
            RemoteCertificateValidationCallback remoteCertificateValidationCallBack,
            LocalCertificateSelectionCallback localCertificateSelectionCallback)
        { 
            //not in cache so create and return
            return await CreateClient(hostname, port, isHttps, version, connectionTimeOutSeconds, upStreamHttpProxy, upStreamHttpsProxy, bufferSize, supportedSslProtocols,
                               remoteCertificateValidationCallBack, localCertificateSelectionCallback);

        }


        /// <summary>
        /// Create connection to a particular host/port optionally with SSL and a particular HTTP version
        /// </summary>
        /// <param name="hostname"></param>
        /// <param name="port"></param>
        /// <param name="isHttps"></param>
        /// <param name="version"></param>
        /// <returns></returns>
        private async Task<TcpConnection> CreateClient(string hostname, int port, bool isHttps, Version version, int connectionTimeOutSeconds,
           ExternalProxy upStreamHttpProxy, ExternalProxy upStreamHttpsProxy, int bufferSize, SslProtocols supportedSslProtocols,
           RemoteCertificateValidationCallback remoteCertificateValidationCallBack, LocalCertificateSelectionCallback localCertificateSelectionCallback)
        {
            TcpClient client;
            Stream stream;

            if (isHttps)
            {
                SslStream sslStream = null;

                //If this proxy uses another external proxy then create a tunnel request for HTTPS connections
                if (upStreamHttpsProxy != null)
                {
                    client = new TcpClient(upStreamHttpsProxy.HostName, upStreamHttpsProxy.Port);
                    stream = client.GetStream();

                    using (var writer = new StreamWriter(stream, Encoding.ASCII, bufferSize, true))
                    {
                        await writer.WriteLineAsync(string.Format("CONNECT {0}:{1} {2}", hostname, port, version));
                        await writer.WriteLineAsync(string.Format("Host: {0}:{1}", hostname, port));
                        await writer.WriteLineAsync("Connection: Keep-Alive");
                        await writer.WriteLineAsync();
                        await writer.FlushAsync();
                        writer.Close();
                    }

                    using (var reader = new CustomBinaryReader(stream))
                    {
                        var result = await reader.ReadLineAsync();

                        if (!result.ToLower().Contains("200 connection established"))
                        {
                            throw new Exception("Upstream proxy failed to create a secure tunnel");
                        }

                        await reader.ReadAllLinesAsync();
                    }
                }
                else
                {
                    client = new TcpClient(hostname, port);
                    stream = client.GetStream();
                }

                try
                {
                    sslStream = new SslStream(stream, true, remoteCertificateValidationCallBack,
                        localCertificateSelectionCallback);

                    await sslStream.AuthenticateAsClientAsync(hostname, null, supportedSslProtocols, false);

                    stream = sslStream;
                }
                catch
                {
                    if (sslStream != null)
                    {
                        sslStream.Dispose();
                    }

                    throw;
                }
            }
            else
            {
                if (upStreamHttpProxy != null)
                {
                    client = new TcpClient(upStreamHttpProxy.HostName, upStreamHttpProxy.Port);
                    stream = client.GetStream();
                }
                else
                {
                    client = new TcpClient(hostname, port);
                    stream = client.GetStream();
                }
            }

            client.ReceiveTimeout = connectionTimeOutSeconds * 1000;
            client.SendTimeout = connectionTimeOutSeconds * 1000;

            stream.ReadTimeout = connectionTimeOutSeconds * 1000;
            stream.WriteTimeout = connectionTimeOutSeconds * 1000;

            return new TcpConnection()
            {
                HostName = hostname,
                port = port,
                IsHttps = isHttps,
                TcpClient = client,
                StreamReader = new CustomBinaryReader(stream),
                Stream = stream,
                Version = version
            };
        }


    }
}

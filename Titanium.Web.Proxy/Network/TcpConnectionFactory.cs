using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Net.Security;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;
using System.Security.Authentication;
using System.Linq;

namespace Titanium.Web.Proxy.Network
{
    /// <summary>
    /// A class that manages Tcp Connection to server used by this proxy server
    /// </summary>
    internal class TcpConnectionFactory
    {

        /// <summary>
        /// Creates a TCP connection to server
        /// </summary>
        /// <param name="bufferSize"></param>
        /// <param name="connectionTimeOutSeconds"></param>
        /// <param name="remoteHostName"></param>
        /// <param name="httpCmd"></param>
        /// <param name="httpVersion"></param>
        /// <param name="isHttps"></param>
        /// <param name="remotePort"></param>
        /// <param name="supportedSslProtocols"></param>
        /// <param name="remoteCertificateValidationCallback"></param>
        /// <param name="localCertificateSelectionCallback"></param>
        /// <param name="externalHttpProxy"></param>
        /// <param name="externalHttpsProxy"></param>
        /// <param name="clientStream"></param>
        /// <returns></returns>
        internal async Task<TcpConnection> CreateClient(int bufferSize, int connectionTimeOutSeconds,
            string remoteHostName, int remotePort, Version httpVersion,
            bool isHttps, SslProtocols supportedSslProtocols,
            RemoteCertificateValidationCallback remoteCertificateValidationCallback, LocalCertificateSelectionCallback localCertificateSelectionCallback,
            ExternalProxy externalHttpProxy, ExternalProxy externalHttpsProxy,
            Stream clientStream)
        {
            TcpClient client;
            Stream stream;

            if (isHttps)
            {
                SslStream sslStream = null;

                //If this proxy uses another external proxy then create a tunnel request for HTTPS connections
                if (externalHttpsProxy != null && externalHttpsProxy.HostName != remoteHostName)
                {
                    client = new TcpClient(externalHttpsProxy.HostName, externalHttpsProxy.Port);
                    stream = client.GetStream();

                    using (var writer = new StreamWriter(stream, Encoding.ASCII, bufferSize, true) { NewLine = "\r\n" })
                    {
                        await writer.WriteLineAsync($"CONNECT {remoteHostName}:{remotePort} HTTP/{httpVersion}");
                        await writer.WriteLineAsync($"Host: {remoteHostName}:{remotePort}");
                        await writer.WriteLineAsync("Connection: Keep-Alive");

                        if (!string.IsNullOrEmpty(externalHttpsProxy.UserName) && externalHttpsProxy.Password != null)
                        {
                            await writer.WriteLineAsync("Proxy-Connection: keep-alive");
                            await writer.WriteLineAsync("Proxy-Authorization" + ": Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(externalHttpsProxy.UserName + ":" + externalHttpsProxy.Password)));
                        }
                        await writer.WriteLineAsync();
                        await writer.FlushAsync();
                        writer.Close();
                    }

                    using (var reader = new CustomBinaryReader(stream))
                    {
                        var result = await reader.ReadLineAsync();


                        if (!new string[] { "200 OK", "connection established" }.Any(s=> result.ToLower().Contains(s.ToLower())))
                        {
                            throw new Exception("Upstream proxy failed to create a secure tunnel");
                        }

                        await reader.ReadAllLinesAsync();
                    }
                }
                else
                {
                    client = new TcpClient(remoteHostName, remotePort);
                    stream = client.GetStream();
                }

                try
                {
                    sslStream = new SslStream(stream, true, remoteCertificateValidationCallback,
                        localCertificateSelectionCallback);

                    await sslStream.AuthenticateAsClientAsync(remoteHostName, null, supportedSslProtocols, false);

                    stream = sslStream;
                }
                catch
                {
                    sslStream?.Dispose();

                    throw;
                }
            }
            else
            {
                if (externalHttpProxy != null && externalHttpProxy.HostName != remoteHostName)
                {
                    client = new TcpClient(externalHttpProxy.HostName, externalHttpProxy.Port);
                    stream = client.GetStream();
                }
                else
                {
                    client = new TcpClient(remoteHostName, remotePort);
                    stream = client.GetStream();
                }
            }

            client.ReceiveTimeout = connectionTimeOutSeconds * 1000;
            client.SendTimeout = connectionTimeOutSeconds * 1000;

            stream.ReadTimeout = connectionTimeOutSeconds * 1000;
            stream.WriteTimeout = connectionTimeOutSeconds * 1000;

            return new TcpConnection()
            {
                UpStreamHttpProxy = externalHttpProxy,
                UpStreamHttpsProxy = externalHttpsProxy,
                HostName = remoteHostName,
                Port = remotePort,
                IsHttps = isHttps,
                TcpClient = client,
                StreamReader = new CustomBinaryReader(stream),
                Stream = stream,
                Version = httpVersion
            };
        }
    }
}

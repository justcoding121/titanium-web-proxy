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
using Titanium.Web.Proxy.Shared;
using Titanium.Web.Proxy.Models;
using System.Security.Authentication;

namespace Titanium.Web.Proxy.Network
{
    /// <summary>
    /// A class that manages Tcp Connection to server used by this proxy server
    /// </summary>
    internal class TcpConnectionManager
    {
        /// <summary>
        /// Connection cache
        /// </summary>
        internal Dictionary<string, List<CachedTcpConnection>> connectionCache = new Dictionary<string, List<CachedTcpConnection>>();

        /// <summary>
        /// A lock to manage concurrency
        /// </summary>
        SemaphoreSlim connectionAccessLock = new SemaphoreSlim(1);

        /// <summary>
        /// Get a TcpConnection to the specified host, port optionally HTTPS and a particular HTTP version
        /// </summary>
        /// <param name="hostname"></param>
        /// <param name="port"></param>
        /// <param name="isHttps"></param>
        /// <param name="version"></param>
        /// <returns></returns>
        internal async Task<CachedTcpConnection> GetClient(string hostname, int port, bool isHttps, Version version,
            ExternalProxy upStreamHttpProxy, ExternalProxy upStreamHttpsProxy, int bufferSize, SslProtocols supportedSslProtocols,
            int connectionTimeOutSeconds,
            RemoteCertificateValidationCallback remoteCertificateValidationCallBack, 
            LocalCertificateSelectionCallback localCertificateSelectionCallback)
        {
            List<CachedTcpConnection> cachedConnections = null;
            CachedTcpConnection cached = null;

            //Get a unique string to identify this connection
            var key = GetConnectionKey(hostname, port, isHttps, version);

            while (true)
            {
                await connectionAccessLock.WaitAsync();

                try
                {
                    connectionCache.TryGetValue(key, out cachedConnections);

                    if (cachedConnections != null && cachedConnections.Count > 0)
                    {
                        cached = cachedConnections.First();
                        cachedConnections.Remove(cached);
                    }
                    else
                    {
                        cached = null;
                    }
                }
                finally {
                    connectionAccessLock.Release();
                }

                if (cached != null && !cached.TcpClient.Client.IsConnected())
                {
                    cached.TcpClient.Client.Dispose();
                    cached.TcpClient.Close();
                    continue;
                }

                if (cached == null)
                {
                    break;
                }

            }


            if (cached == null)
            {
                cached = await CreateClient(hostname, port, isHttps, version, connectionTimeOutSeconds, upStreamHttpProxy, upStreamHttpsProxy, bufferSize, supportedSslProtocols,
                                remoteCertificateValidationCallBack, localCertificateSelectionCallback);
            }

            return cached;
        }

        /// <summary>
        /// Get a string to identfiy the connection to a particular host, port
        /// </summary>
        /// <param name="hostname"></param>
        /// <param name="port"></param>
        /// <param name="isHttps"></param>
        /// <param name="version"></param>
        /// <returns></returns>
        internal string GetConnectionKey(string hostname, int port, bool isHttps, Version version)
        {
            return string.Format("{0}:{1}:{2}:{3}:{4}", hostname.ToLower(), port, isHttps, version.Major, version.Minor);
        }

        /// <summary>
        /// Create connection to a particular host/port optionally with SSL and a particular HTTP version
        /// </summary>
        /// <param name="hostname"></param>
        /// <param name="port"></param>
        /// <param name="isHttps"></param>
        /// <param name="version"></param>
        /// <returns></returns>
        private async Task<CachedTcpConnection> CreateClient(string hostname, int port, bool isHttps, Version version, int connectionTimeOutSeconds,
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

            return new CachedTcpConnection()
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

        /// <summary>
        /// Returns a Tcp Connection back to cache for reuse by other requests
        /// </summary>
        /// <param name="connection"></param>
        /// <returns></returns>
        internal async Task ReleaseClient(CachedTcpConnection connection)
        {

            connection.LastAccess = DateTime.Now;
            var key = GetConnectionKey(connection.HostName, connection.port, connection.IsHttps, connection.Version);
            await connectionAccessLock.WaitAsync();

            try
            {
                List<CachedTcpConnection> cachedConnections;
                connectionCache.TryGetValue(key, out cachedConnections);

                if (cachedConnections != null)
                {
                    cachedConnections.Add(connection);
                }
                else
                {
                    connectionCache.Add(key, new List<CachedTcpConnection>() { connection });
                }
            }

            finally {
                connectionAccessLock.Release();
            }
        }

        private bool clearConenctions { get; set; }

        /// <summary>
        /// Stop clearing idle connections
        /// </summary>
        internal void StopClearIdleConnections()
        {
            clearConenctions = false;
        }

        /// <summary>
        /// A method to clear idle connections
        /// </summary>
        internal async void ClearIdleConnections(int connectionCacheTimeOutMinutes)
        {
            clearConenctions = true;

            while (clearConenctions)
            {
                await connectionAccessLock.WaitAsync();

                try
                {
                    var cutOff = DateTime.Now.AddMinutes(-1 * connectionCacheTimeOutMinutes);

                    connectionCache
                       .SelectMany(x => x.Value)
                       .Where(x => x.LastAccess < cutOff)
                       .ToList()
                       .ForEach(x => x.TcpClient.Close());

                    connectionCache.ToList().ForEach(x => x.Value.RemoveAll(y => y.LastAccess < cutOff));
                }
                finally {
                    connectionAccessLock.Release();
                }

                //every minute run this 
                await Task.Delay(1000 * 60);
            }

        }

    }
}

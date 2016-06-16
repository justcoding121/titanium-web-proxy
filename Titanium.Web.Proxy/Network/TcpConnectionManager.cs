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
        static Dictionary<string, List<TcpConnection>> connectionCache = new Dictionary<string, List<TcpConnection>>();

        /// <summary>
        /// A lock to manage concurrency
        /// </summary>
        static SemaphoreSlim connectionAccessLock = new SemaphoreSlim(1);

        /// <summary>
        /// Get a TcpConnection to the specified host, port optionally HTTPS and a particular HTTP version
        /// </summary>
        /// <param name="hostname"></param>
        /// <param name="port"></param>
        /// <param name="isHttps"></param>
        /// <param name="version"></param>
        /// <returns></returns>
        internal static async Task<TcpConnection> GetClient(string hostname, int port, bool isHttps, Version version)
        {
            List<TcpConnection> cachedConnections = null;
            TcpConnection cached = null;

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
                finally { connectionAccessLock.Release(); }

                if (cached != null && !cached.TcpClient.Client.IsConnected())
                {
                    cached.TcpClient.Client.Dispose();
                    cached.TcpClient.Close();
                    continue;
                }

                if (cached == null)
                    break;

            }

            if (cached == null)
                cached = await CreateClient(hostname, port, isHttps, version);

            if (cachedConnections == null || cachedConnections.Count() <= 2)
            {
                var task = CreateClient(hostname, port, isHttps, version)
                              .ContinueWith(async (x) => { if (x.Status == TaskStatus.RanToCompletion) await ReleaseClient(x.Result); });
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
        internal static string GetConnectionKey(string hostname, int port, bool isHttps, Version version)
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
        private static async Task<TcpConnection> CreateClient(string hostname, int port, bool isHttps, Version version)
        {
            TcpClient client;
            Stream stream;

            if (isHttps)
            {
                SslStream sslStream = null;

                //If this proxy uses another external proxy then create a tunnel request for HTTPS connections
                if (ProxyServer.UpStreamHttpsProxy != null)
                {
                    client = new TcpClient(ProxyServer.UpStreamHttpsProxy.HostName, ProxyServer.UpStreamHttpsProxy.Port);
                    stream = (Stream)client.GetStream();

                    using (var writer = new StreamWriter(stream, Encoding.ASCII, ProxyConstants.BUFFER_SIZE, true))
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
                            throw new Exception("Upstream proxy failed to create a secure tunnel");

                        await reader.ReadAllLinesAsync();
                    }
                }
                else
                {
                    client = new TcpClient(hostname, port);
                    stream = (Stream)client.GetStream();
                }

                try
                {
                    sslStream = new SslStream(stream, true, new RemoteCertificateValidationCallback(ProxyServer.ValidateServerCertificate),
                        new LocalCertificateSelectionCallback(ProxyServer.SelectClientCertificate));
                    await sslStream.AuthenticateAsClientAsync(hostname, null, ProxyConstants.SupportedSslProtocols, false);
                    stream = (Stream)sslStream;
                }
                catch
                {
                    if (sslStream != null)
                        sslStream.Dispose();
                    throw;
                }
            }
            else
            {
                if (ProxyServer.UpStreamHttpProxy != null)
                {
                    client = new TcpClient(ProxyServer.UpStreamHttpProxy.HostName, ProxyServer.UpStreamHttpProxy.Port);
                    stream = (Stream)client.GetStream();
                }
                else
                {
                    client = new TcpClient(hostname, port);
                    stream = (Stream)client.GetStream();
                }
            }

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

        /// <summary>
        /// Returns a Tcp Connection back to cache for reuse by other requests
        /// </summary>
        /// <param name="connection"></param>
        /// <returns></returns>
        internal static async Task ReleaseClient(TcpConnection connection)
        {

            connection.LastAccess = DateTime.Now;
            var key = GetConnectionKey(connection.HostName, connection.port, connection.IsHttps, connection.Version);
            await connectionAccessLock.WaitAsync();
            try
            {
                List<TcpConnection> cachedConnections;
                connectionCache.TryGetValue(key, out cachedConnections);

                if (cachedConnections != null)
                    cachedConnections.Add(connection);
                else

                    connectionCache.Add(key, new List<TcpConnection>() { connection });
            }

            finally { connectionAccessLock.Release(); }
        }

        private static bool clearConenctions { get; set; }

        /// <summary>
        /// Stop clearing idle connections
        /// </summary>
        internal static void StopClearIdleConnections()
        {
            clearConenctions = false;
        }

        /// <summary>
        /// A method to clear idle connections
        /// </summary>
        internal async static void ClearIdleConnections()
        {
            clearConenctions = true;
            while (clearConenctions)
            {
                await connectionAccessLock.WaitAsync();
                try
                {
                    var cutOff = DateTime.Now.AddMinutes(-1 * ProxyServer.ConnectionCacheTimeOutMinutes);

                    connectionCache
                       .SelectMany(x => x.Value)
                       .Where(x => x.LastAccess < cutOff)
                       .ToList()
                       .ForEach(x => x.TcpClient.Close());

                    connectionCache.ToList().ForEach(x => x.Value.RemoveAll(y => y.LastAccess < cutOff));
                }
                finally { connectionAccessLock.Release(); }

                //every minute run this 
                await Task.Delay(1000 * 60);
            }

        }

    }
}

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
using System.Security.Cryptography.X509Certificates;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Network
{
    public class TcpConnection
    {
        internal string HostName { get; set; }
        internal int port { get; set; }
        internal bool IsHttps { get; set; }
        internal Version Version { get; set; }

        internal TcpClient TcpClient { get; set; }
        internal CustomBinaryReader StreamReader { get; set; }
        internal Stream Stream { get; set; }

        internal DateTime LastAccess { get; set; }

        internal TcpConnection()
        {
            LastAccess = DateTime.Now;
        }
    }

    internal class TcpConnectionManager
    {
        static Dictionary<string, List<TcpConnection>> connectionCache = new Dictionary<string, List<TcpConnection>>();
        static SemaphoreSlim connectionAccessLock = new SemaphoreSlim(1);


        internal static async Task<TcpConnection> GetClient(string hostname, int port, bool isHttps, Version version)
        {
            List<TcpConnection> cachedConnections = null;
            TcpConnection cached = null;

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
                cached = await CreateClient(hostname, port, isHttps, version).ConfigureAwait(false);

            if (cachedConnections == null || cachedConnections.Count() <= 2)
            {
                var task = CreateClient(hostname, port, isHttps, version)
                              .ContinueWith(async (x) => { if (x.Status == TaskStatus.RanToCompletion) await ReleaseClient(x.Result); });
            }

            return cached;
        }

        internal static string GetConnectionKey(string hostname, int port, bool isHttps, Version version)
        {
            return string.Format("{0}:{1}:{2}:{3}:{4}", hostname.ToLower(), port, isHttps, version.Major, version.Minor);
        }

        private static async Task<TcpConnection> CreateClient(string hostname, int port, bool isHttps, Version version)
        {
            TcpClient client;
            Stream stream;

            if (isHttps)
            {
                CustomSslStream sslStream = null;

                if (ProxyServer.UpStreamHttpsProxy != null)
                {
                    client = new TcpClient(ProxyServer.UpStreamHttpsProxy.HostName, ProxyServer.UpStreamHttpsProxy.Port);
                    stream = (Stream)client.GetStream();

                    using (var writer = new StreamWriter(stream, Encoding.ASCII, Constants.BUFFER_SIZE, true))
                    {
                        await writer.WriteLineAsync(string.Format("CONNECT {0}:{1} {2}", hostname, port, version)).ConfigureAwait(false);
                        await writer.WriteLineAsync(string.Format("Host: {0}:{1}", hostname, port)).ConfigureAwait(false);
                        await writer.WriteLineAsync("Connection: Keep-Alive").ConfigureAwait(false);
                        await writer.WriteLineAsync().ConfigureAwait(false);
                        await writer.FlushAsync().ConfigureAwait(false);
                        writer.Close();
                    }

                    using (var reader = new CustomBinaryReader(stream))
                    {
                        var result = await reader.ReadLineAsync().ConfigureAwait(false);

                        if (!result.ToLower().Contains("200 connection established"))
                            throw new Exception("Upstream proxy failed to create a secure tunnel");

                        await reader.ReadAllLinesAsync().ConfigureAwait(false);
                    }
                }
                else
                {
                    client = new TcpClient(hostname, port);
                    stream = (Stream)client.GetStream();
                }

                try
                {
                    sslStream = new CustomSslStream(stream, true, new RemoteCertificateValidationCallback(ProxyServer.ValidateServerCertificate),
                        new LocalCertificateSelectionCallback(ProxyServer.SelectClientCertificate));
                    await sslStream.AuthenticateAsClientAsync(hostname, null, Constants.SupportedProtocols, false).ConfigureAwait(false);
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

        internal static void StopClearIdleConnections()
        {
            clearConenctions = false;
        }

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

                await Task.Delay(1000 * 60 * 3).ConfigureAwait(false);
            }

        }

    }
}

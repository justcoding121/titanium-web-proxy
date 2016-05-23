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

namespace Titanium.Web.Proxy.Network
{
    public class TcpConnection
    {
        internal string HostName { get; set; }
        internal int port { get; set; }
        internal bool IsSecure { get; set; }
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
        static List<TcpConnection> connectionCache = new List<TcpConnection>();
        static SemaphoreSlim connectionAccessLock = new SemaphoreSlim(1);
        internal static async Task<TcpConnection> GetClient(SessionEventArgs sessionArgs, string hostname, int port, bool isSecure, Version version)
        {
            TcpConnection cached = null;
            while (true)
            {
                await connectionAccessLock.WaitAsync();
                try
                {
                    cached = connectionCache.FirstOrDefault(x => x.HostName == hostname && x.port == port &&
                    x.IsSecure == isSecure && x.TcpClient.Connected && x.Version.Equals(version));

                    //just create one more preemptively
                    if (connectionCache.Where(x => x.HostName == hostname && x.port == port &&
                    x.IsSecure == isSecure && x.TcpClient.Connected && x.Version.Equals(version)).Count() < 2)
                    {
                        var task = CreateClient(sessionArgs, hostname, port, isSecure, version)
                                    .ContinueWith(async (x) => { if (x.Status == TaskStatus.RanToCompletion) await ReleaseClient(x.Result); });
                    }

                    if (cached != null)
                        connectionCache.Remove(cached);
                }
                finally { connectionAccessLock.Release(); }

                if (cached != null && !cached.TcpClient.Client.IsConnected())
                    continue;

                if (cached == null)
                    break;
            }

            if (cached == null)
                cached = await CreateClient(sessionArgs, hostname, port, isSecure, version).ConfigureAwait(false);
       
            return cached;
        }

        private static async Task<TcpConnection> CreateClient(SessionEventArgs sessionArgs, string hostname, int port, bool isSecure, Version version)
        {
            TcpClient client;
            Stream stream;

            if (isSecure)
            {
                CustomSslStream sslStream = null;

                if (ProxyServer.UpStreamHttpsProxy != null)
                {
                    client = new TcpClient(ProxyServer.UpStreamHttpsProxy.HostName, ProxyServer.UpStreamHttpsProxy.Port);
                    stream = (Stream)client.GetStream();

                    using (var writer = new StreamWriter(stream, Encoding.ASCII, Constants.BUFFER_SIZE, true))
                    {
                        await writer.WriteLineAsync(string.Format("CONNECT {0}:{1} {2}", sessionArgs.WebSession.Request.RequestUri.Host, sessionArgs.WebSession.Request.RequestUri.Port, sessionArgs.WebSession.Request.HttpVersion));
                        await writer.WriteLineAsync(string.Format("Host: {0}:{1}", sessionArgs.WebSession.Request.RequestUri.Host, sessionArgs.WebSession.Request.RequestUri.Port));
                        await writer.WriteLineAsync("Connection: Keep-Alive");
                        await writer.WriteLineAsync();
                        await writer.FlushAsync();
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
                    sslStream = new CustomSslStream(stream, true, new RemoteCertificateValidationCallback(ProxyServer.ValidateServerCertificate));
                    sslStream.Session = sessionArgs;
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
                IsSecure = isSecure,
                TcpClient = client,
                StreamReader = new CustomBinaryReader(stream),
                Stream = stream,
                Version = version
            };
        }


        internal static async Task ReleaseClient(TcpConnection Connection)
        {
            Connection.LastAccess = DateTime.Now;
            await connectionAccessLock.WaitAsync();
            try
            {
                connectionCache.Add(Connection);
            }
            finally { connectionAccessLock.Release(); }
        }

        internal async static void ClearIdleConnections()
        {
            while (true)
            {
                await connectionAccessLock.WaitAsync();
                try
                {
                    var cutOff = DateTime.Now.AddSeconds(-60);

                    connectionCache
                       .Where(x => x.LastAccess < cutOff)
                       .ToList()
                       .ForEach(x => x.TcpClient.Close());

                    connectionCache.RemoveAll(x => x.LastAccess < cutOff);
                }
                finally { connectionAccessLock.Release(); }

                await Task.Delay(1000 * 60 * 3).ConfigureAwait(false);
            }

        }

    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.IO;
using System.Net.Security;
using Titanium.Web.Proxy.Helpers;

namespace Titanium.Web.Proxy.Http
{
    public class TcpConnection
    {
        public TcpClient Client { get; set; }
        public CustomBinaryReader ServerStreamReader { get; set; }
        public Stream Stream { get; set; }
    }

    internal class TcpConnectionManager
    {
        static ConcurrentDictionary<string, ConcurrentStack<TcpConnection>> ConnectionCache = new ConcurrentDictionary<string, ConcurrentStack<TcpConnection>>();

        public static async Task<TcpConnection> GetClient(string Hostname, int port, bool IsSecure)
        {
            var key = string.Concat(Hostname, ":", port, ":", IsSecure);

            ConcurrentStack<TcpConnection> connections;
            if (!ConnectionCache.TryGetValue(key, out connections))
            {
                return await CreateClient(Hostname, port, IsSecure);
            }

            TcpConnection client;
            if (!connections.TryPop(out client))
            {
                return await CreateClient(Hostname, port, IsSecure);
            }
            return client;
        }

        private static async Task<TcpConnection> CreateClient(string Hostname, int port, bool IsSecure)
        {
            var client = new TcpClient(Hostname, port);
            var stream = (Stream)client.GetStream();

            if (IsSecure)
            {
                var sslStream = (SslStream)null;
                try
                {
                    sslStream = new SslStream(stream);
                    await AsyncPlatformExtensions.AuthenticateAsClientAsync(sslStream, Hostname);
                    stream = (Stream)sslStream;
                }
                catch
                {
                    if (sslStream != null)
                        sslStream.Dispose();
                    throw;
                }
            }

            return new TcpConnection() { Client = client, ServerStreamReader = new CustomBinaryReader(stream, Encoding.ASCII), Stream = stream };
        }

        public static void AddClient(string Hostname, int port, bool IsSecure, TcpConnection Client)
        {
            var key = string.Concat(Hostname, ":", port, ":", IsSecure);

            ConcurrentStack<TcpConnection> connections;
            if (!ConnectionCache.TryGetValue(key, out connections))
            {
                connections = new ConcurrentStack<TcpConnection>();
                connections.Push(Client);
                ConnectionCache.TryAdd(key, connections);
            }

            connections.Push(Client);

        }


    }
}

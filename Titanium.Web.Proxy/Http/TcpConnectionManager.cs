using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.IO;
using System.Net.Security;

namespace Titanium.Web.Proxy.Http
{

    internal class TcpConnectionManager
    {
        static ConcurrentDictionary<string, ConcurrentStack<TcpClient>> ConnectionCache = new ConcurrentDictionary<string, ConcurrentStack<TcpClient>>();

        public static async Task<TcpClient> GetClient(string Hostname, int port, bool IsSecure)
        {
            var key = string.Concat(Hostname, ":", port, ":", IsSecure);

            ConcurrentStack<TcpClient> connections;
            if (!ConnectionCache.TryGetValue(key, out connections))
            {
                return await CreateClient(Hostname, port, IsSecure);
            }

            TcpClient client;
            if (!connections.TryPop(out client))
            {
                return await CreateClient(Hostname, port, IsSecure);
            }
            return client;
        }

        private static async Task<TcpClient> CreateClient(string Hostname, int port, bool IsSecure)
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
            return client;
        }

        public static void AddClient(string Hostname, int port, bool IsSecure, TcpClient Client)
        {
            var key = string.Concat(Hostname, ":", port, ":", IsSecure);

            ConcurrentStack<TcpClient> connections;
            if (!ConnectionCache.TryGetValue(key, out connections))
            {
                connections = new ConcurrentStack<TcpClient>();
                connections.Push(Client);
                ConnectionCache.TryAdd(key, connections);
            }

            connections.Push(Client);

        }


    }
}

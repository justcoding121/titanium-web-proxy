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
        static Dictionary<string, Stack<TcpConnection>> ConnectionCache = new Dictionary<string, Stack<TcpConnection>>();

        public static TcpConnection GetClient(string Hostname, int port, bool IsSecure)
        {
            var key = string.Concat(Hostname, ":", port, ":", IsSecure);
            TcpConnection client;
            lock (ConnectionCache)
            {
                Stack<TcpConnection> connections;
                if (!ConnectionCache.TryGetValue(key, out connections))
                {
                    return CreateClient(Hostname, port, IsSecure);
                }

                if (connections.Count > 0)
                {
                    client = connections.Pop();
                }
                else
                    return CreateClient(Hostname, port, IsSecure);
            }
            return client;
        }

        private static TcpConnection CreateClient(string Hostname, int port, bool IsSecure)
        {
            var client = new TcpClient(Hostname, port);
            var stream = (Stream)client.GetStream();

            if (IsSecure)
            {
                var sslStream = (SslStream)null;
                try
                {
                    sslStream = new SslStream(stream);
                    sslStream.AuthenticateAsClient(Hostname);
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
            lock (ConnectionCache)
            {

                Stack<TcpConnection> connections;
                if (!ConnectionCache.TryGetValue(key, out connections))
                {
                    connections = new Stack<TcpConnection>();
                    connections.Push(Client);
                    ConnectionCache.Add(key, connections);
                }

                connections.Push(Client);
            }

        }


    }
}

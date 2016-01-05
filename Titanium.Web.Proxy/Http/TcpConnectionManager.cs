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
        public string HostName { get; set; }
        public int port { get; set; }
        public bool IsSecure { get; set; }

        public TcpClient Client { get; set; }
        public CustomBinaryReader ServerStreamReader { get; set; }
        public Stream Stream { get; set; }
    }

    internal class TcpConnectionManager
    {
        static List<TcpConnection> ConnectionCache = new List<TcpConnection>();

        public static TcpConnection GetClient(string Hostname, int port, bool IsSecure)
        {
            lock (ConnectionCache)
            {
                var cached = ConnectionCache.FirstOrDefault(x => x.HostName == Hostname && x.port == port && x.IsSecure == IsSecure && x.Client.Connected);

                if (cached != null)
                {
                    ConnectionCache.Remove(cached);
                    return cached;
                }
            }

            return CreateClient(Hostname, port, IsSecure);
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

            return new TcpConnection()
            {
                HostName = Hostname,
                port = port,
                IsSecure = IsSecure,
                Client = client,
                ServerStreamReader = new CustomBinaryReader(stream, Encoding.ASCII),
                Stream = stream
            };
        }

        public static void ReleaseClient(TcpConnection Connection)
        {
            ConnectionCache.Add(Connection);
        }

    }
}

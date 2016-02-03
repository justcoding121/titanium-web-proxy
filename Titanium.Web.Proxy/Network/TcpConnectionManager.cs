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
using System.Threading;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Titanium.Web.Proxy.Network
{
    public class TcpConnection
    {
        public string HostName { get; set; }
        public int port { get; set; }
        public bool IsSecure { get; set; }

        public TcpClient TcpClient { get; set; }
        public CustomBinaryReader ServerStreamReader { get; set; }
        public Stream Stream { get; set; }

        public DateTime LastAccess { get; set; }

        public TcpConnection()
        {
            LastAccess = DateTime.Now;
        }
    }

    internal class TcpConnectionManager
    {
        static List<TcpConnection> ConnectionCache = new List<TcpConnection>();

        public static TcpConnection GetClient(string Hostname, int port, bool IsSecure, Stream clientStream)
        {
            TcpConnection cached = null;
            SslProtocols protocol = SslProtocols.None;

            while (true)
            {
                lock (ConnectionCache)
                {
                    cached = ConnectionCache.FirstOrDefault(x => x.HostName == Hostname && x.port == port && x.IsSecure == IsSecure && x.TcpClient.Connected);

                    if (cached != null)
                        ConnectionCache.Remove(cached);
                }

                if (cached != null && !cached.TcpClient.Client.IsConnected())
                    continue;

                if (cached == null)
                    break;
            }

            if (IsSecure && clientStream is SslStream)
            {
                protocol = ((SslStream)clientStream).SslProtocol;
            }

            if (cached == null)
            {
                cached = CreateClient(Hostname, port, IsSecure, protocol);
            }

            if (ConnectionCache.Where(x => x.HostName == Hostname && x.port == port && x.IsSecure == IsSecure && x.TcpClient.Connected).Count() < 2)
            {
                Task.Factory.StartNew(() => ReleaseClient(CreateClient(Hostname, port, IsSecure, protocol)));
            }

            return cached;
        }

        private static TcpConnection CreateClient(string Hostname, int port, bool IsSecure, SslProtocols protocol)
        {
            TcpClient client = new TcpClient(Hostname, port);
            Stream stream = client.GetStream();

            if (IsSecure)
            {
                SslStream sslStream = null;
                try
                {
                    if (!ProxyServer.CheckCertificateRevocation)
                    {
                        sslStream = new SslStream(stream, false, TrustAllCertificatesCallback);
                    }
                    else
                    {
                        sslStream = new SslStream(stream);
                    }
                    sslStream.AuthenticateAsClient(Hostname, null, protocol, ProxyServer.CheckCertificateRevocation);
                    stream = (Stream)sslStream;
                }
                catch (Exception exception)
                {
                    Console.WriteLine(exception.Message);
                    if (sslStream != null)
                        sslStream.Dispose();
                    throw exception;
                }
            }

            return new TcpConnection()
            {
                HostName = Hostname,
                port = port,
                IsSecure = IsSecure,
                TcpClient = client,
                ServerStreamReader = new CustomBinaryReader(stream, Encoding.ASCII),
                Stream = stream
            };
        }

        private static bool TrustAllCertificatesCallback(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            return true;
        }

        public static void ReleaseClient(TcpConnection Connection)
        {
            Connection.LastAccess = DateTime.Now;
            ConnectionCache.Add(Connection);
        }

        public static void ClearIdleConnections()
        {
            while (true)
            {
                lock (ConnectionCache)
                {
                    var cutOff = DateTime.Now.AddSeconds(-60);

                    ConnectionCache
                       .Where(x => x.LastAccess < cutOff)
                       .ToList()
                       .ForEach(x => x.TcpClient.Close());

                    ConnectionCache.RemoveAll(x => x.LastAccess < cutOff);
                }

                Thread.Sleep(1000 * 60 * 3);
            }

        }


    }
}

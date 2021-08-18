using System.Threading;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.EventArguments
{
    /// <summary>
    ///     This is used in transparent endpoint before authenticating client.
    /// </summary>
    public class BeforeSslAuthenticateEventArgs : ProxyEventArgsBase
    {
        internal readonly CancellationTokenSource TaskCancellationSource;

        internal BeforeSslAuthenticateEventArgs(ProxyServer server, TcpClientConnection clientConnection, CancellationTokenSource taskCancellationSource, string sniHostName) : base(server, clientConnection)
        {
            TaskCancellationSource = taskCancellationSource;
            SniHostName = sniHostName;
            ForwardHttpsHostName = sniHostName;
        }

        /// <summary>
        ///     The server name indication hostname if available. 
        ///     Otherwise the GenericCertificateName property of TransparentEndPoint.
        /// </summary>
        public string SniHostName { get; }

        /// <summary>
        ///     Should we decrypt the SSL request?
        ///     If true we decrypt with fake certificate.
        ///     If false we relay the connection to the hostname mentioned in SniHostname.
        /// </summary>
        public bool DecryptSsl { get; set; } = true;

        /// <summary>
        /// We need to know the server hostname we are forwarding the request to.
        /// By default its the SNI hostname indicated in SSL handshake, when SNI is available.
        /// When SNI is not available, it will use the GenericCertificateName of TransparentEndPoint.
        /// This property is used only when DecryptSsl or when BeforeSslAuthenticateEventArgs.DecryptSsl is false.
        /// When DecryptSsl is true, we need to explicitly set the Forwarded host and port by setting 
        /// e.HttpClient.Request.Url inside BeforeRequest event handler.
        /// </summary>
        public string ForwardHttpsHostName { get; set; }

        /// <summary>
        /// We need to know the server port we are forwarding the request to.
        /// By default its the standard https port, 443. 
        /// This property is used only when DecryptSsl or when BeforeSslAuthenticateEventArgs.DecryptSsl is false.
        /// When DecryptSsl is true, we need to explicitly set the Forwarded host and port by setting 
        /// e.HttpClient.Request.Url inside BeforeRequest event handler.
        /// </summary>
        public int ForwardHttpsPort { get; set; } = 443;

        /// <summary>
        ///     Terminate the request abruptly by closing client/server connections.
        /// </summary>
        public void TerminateSession()
        {
            TaskCancellationSource.Cancel();
        }
    }
}

using System;
using System.Net;
using System.Threading;
using StreamExtended;
using StreamExtended.Network;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.EventArguments
{
    /// <summary>
    ///     Holds info related to a single proxy session (single request/response sequence).
    ///     A proxy session is bounded to a single connection from client.
    ///     A proxy session ends when client terminates connection to proxy
    ///     or when server terminates connection from proxy.
    /// </summary>
    public abstract class SessionEventArgsBase : EventArgs, IDisposable
    {

        internal readonly CancellationTokenSource CancellationTokenSource;

        protected readonly int bufferSize;
        protected readonly IBufferPool bufferPool;
        protected readonly ExceptionHandler exceptionFunc;

        /// <summary>
        ///     Initializes a new instance of the <see cref="SessionEventArgsBase" /> class.
        /// </summary>
        private SessionEventArgsBase(ProxyServer server, ProxyEndPoint endPoint,
            CancellationTokenSource cancellationTokenSource)
        {
            bufferSize = server.BufferSize;
            bufferPool = server.BufferPool;
            exceptionFunc = server.ExceptionFunc;
        }

        protected SessionEventArgsBase(ProxyServer server, ProxyEndPoint endPoint,
            CancellationTokenSource cancellationTokenSource,
            Request request) : this(server, endPoint, cancellationTokenSource)
        {
            CancellationTokenSource = cancellationTokenSource;

            ProxyClient = new ProxyClient();
            WebSession = new HttpWebClient(request);
            LocalEndPoint = endPoint;

            WebSession.ProcessId = new Lazy<int>(() =>
            {
                if (RunTime.IsWindows)
                {
                    var remoteEndPoint = ClientEndPoint;

                    // If client is localhost get the process id
                    if (NetworkHelper.IsLocalIpAddress(remoteEndPoint.Address))
                    {
                        var ipVersion = endPoint.IpV6Enabled ? IpVersion.Ipv6 : IpVersion.Ipv4;
                        return TcpHelper.GetProcessIdByLocalPort(ipVersion, remoteEndPoint.Port);
                    }

                    // can't access process Id of remote request from remote machine
                    return -1;
                }

                throw new PlatformNotSupportedException();
            });
        }

        /// <summary>
        ///     Holds a reference to client
        /// </summary>
        internal ProxyClient ProxyClient { get; }

        /// <summary>
        ///     Returns a user data for this request/response session which is
        ///     same as the user data of WebSession.
        /// </summary>
        public object UserData
        {
            get => WebSession.UserData;
            set => WebSession.UserData = value;
        }

        /// <summary>
        ///     Does this session uses SSL?
        /// </summary>
        public bool IsHttps => WebSession.Request.IsHttps;

        /// <summary>
        ///     Client End Point.
        /// </summary>
        public IPEndPoint ClientEndPoint => (IPEndPoint)ProxyClient.ClientConnection.RemoteEndPoint;

        /// <summary>
        ///     A web session corresponding to a single request/response sequence
        ///     within a proxy connection.
        /// </summary>
        public HttpWebClient WebSession { get; }

        /// <summary>
        ///     Are we using a custom upstream HTTP(S) proxy?
        /// </summary>
        public ExternalProxy CustomUpStreamProxyUsed { get; internal set; }

        /// <summary>
        ///     Local endpoint via which we make the request.
        /// </summary>
        public ProxyEndPoint LocalEndPoint { get; }

        /// <summary>
        ///     Is this a transparent endpoint?
        /// </summary>
        public bool IsTransparent => LocalEndPoint is TransparentProxyEndPoint;

        /// <summary>
        ///     The last exception that happened.
        /// </summary>
        public Exception Exception { get; internal set; }

        /// <summary>
        ///     Implements cleanup here.
        /// </summary>
        public virtual void Dispose()
        {
            CustomUpStreamProxyUsed = null;

            DataSent = null;
            DataReceived = null;
            Exception = null;

            WebSession.FinishSession();
        }

        /// <summary>
        ///     Fired when data is sent within this session to server/client.
        /// </summary>
        public event EventHandler<DataEventArgs> DataSent;

        /// <summary>
        ///     Fired when data is received within this session from client/server.
        /// </summary>
        public event EventHandler<DataEventArgs> DataReceived;

        internal void OnDataSent(byte[] buffer, int offset, int count)
        {
            try
            {
                DataSent?.Invoke(this, new DataEventArgs(buffer, offset, count));
            }
            catch (Exception ex)
            {
                exceptionFunc(new Exception("Exception thrown in user event", ex));
            }
        }

        internal void OnDataReceived(byte[] buffer, int offset, int count)
        {
            try
            {
                DataReceived?.Invoke(this, new DataEventArgs(buffer, offset, count));
            }
            catch (Exception ex)
            {
                exceptionFunc(new Exception("Exception thrown in user event", ex));
            }
        }

        /// <summary>
        ///     Terminates the session abruptly by terminating client/server connections.
        /// </summary>
        public void TerminateSession()
        {
            CancellationTokenSource.Cancel();
        }
    }
}

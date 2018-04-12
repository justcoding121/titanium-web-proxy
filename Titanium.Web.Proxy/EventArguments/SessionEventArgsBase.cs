using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using StreamExtended.Helpers;
using StreamExtended.Network;
using Titanium.Web.Proxy.Decompression;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http.Responses;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.EventArguments
{
    /// <summary>
    /// Holds info related to a single proxy session (single request/response sequence)
    /// A proxy session is bounded to a single connection from client
    /// A proxy session ends when client terminates connection to proxy
    /// or when server terminates connection from proxy
    /// </summary>
    public class SessionEventArgsBase : EventArgs, IDisposable
    {
        /// <summary>
        /// Size of Buffers used by this object
        /// </summary>
        protected readonly int BufferSize;

        protected readonly ExceptionHandler ExceptionFunc;

        /// <summary>
        /// Holds a reference to client
        /// </summary>
        internal ProxyClient ProxyClient { get; }

        /// <summary>
        /// Returns a unique Id for this request/response session
        /// same as RequestId of WebSession
        /// </summary>
        public Guid Id => WebSession.RequestId;

        /// <summary>
        /// Does this session uses SSL
        /// </summary>
        public bool IsHttps => WebSession.Request.IsHttps;

        /// <summary>
        /// Client End Point.
        /// </summary>
        public IPEndPoint ClientEndPoint => (IPEndPoint)ProxyClient.TcpClient.Client.RemoteEndPoint;

        /// <summary>
        /// A web session corresponding to a single request/response sequence
        /// within a proxy connection
        /// </summary>
        public HttpWebClient WebSession { get; }

        /// <summary>
        /// Are we using a custom upstream HTTP(S) proxy?
        /// </summary>
        public ExternalProxy CustomUpStreamProxyUsed { get; internal set; }

        public event EventHandler<DataEventArgs> DataSent;

        public event EventHandler<DataEventArgs> DataReceived;

        public ProxyEndPoint LocalEndPoint { get; }

        public bool IsTransparent => LocalEndPoint is TransparentProxyEndPoint;

        public Exception Exception { get; internal set; }

        /// <summary>
        /// Constructor to initialize the proxy
        /// </summary>
        internal SessionEventArgsBase(int bufferSize, ProxyEndPoint endPoint, ExceptionHandler exceptionFunc)
            : this(bufferSize, endPoint, exceptionFunc, null)
        {
        }

        protected SessionEventArgsBase(int bufferSize, ProxyEndPoint endPoint, ExceptionHandler exceptionFunc, Request request)
        {
            this.BufferSize = bufferSize;
            this.ExceptionFunc = exceptionFunc;

            ProxyClient = new ProxyClient();
            WebSession = new HttpWebClient(bufferSize, request);
            LocalEndPoint = endPoint;

            WebSession.ProcessId = new Lazy<int>(() =>
            {
                if (RunTime.IsWindows)
                {
                    var remoteEndPoint = (IPEndPoint)ProxyClient.TcpClient.Client.RemoteEndPoint;

                    //If client is localhost get the process id
                    if (NetworkHelper.IsLocalIpAddress(remoteEndPoint.Address))
                    {
                        return NetworkHelper.GetProcessIdFromPort(remoteEndPoint.Port, endPoint.IpV6Enabled);
                    }

                    //can't access process Id of remote request from remote machine
                    return -1;
                }

                throw new PlatformNotSupportedException();
            });
        }

        internal void OnDataSent(byte[] buffer, int offset, int count)
        {
            try
            {
                DataSent?.Invoke(this, new DataEventArgs(buffer, offset, count));
            }
            catch (Exception ex)
            {
                ExceptionFunc(new Exception("Exception thrown in user event", ex));
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
                ExceptionFunc(new Exception("Exception thrown in user event", ex));
            }
        }

        /// <summary>
        /// implement any cleanup here
        /// </summary>
        public virtual void Dispose()
        {
            CustomUpStreamProxyUsed = null;

            DataSent = null;
            DataReceived = null;
            Exception = null;

            WebSession.FinishSession();
        }
    }
}

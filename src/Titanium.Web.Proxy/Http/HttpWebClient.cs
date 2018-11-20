using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy.Http
{
    /// <summary>
    ///     Used to communicate with the server over HTTP(S)
    /// </summary>
    public class HttpWebClient
    {
        internal HttpWebClient(Request request)
        {
            Request = request ?? new Request();
            Response = new Response();
        }

        /// <summary>
        ///     Connection to server
        /// </summary>
        internal TcpServerConnection Connection { get; set; }

        /// <summary>
        ///     Should we close the server connection at the end of this HTTP request/response session.
        /// </summary>
        internal bool CloseServerConnection { get; set; }

        /// <summary>
        ///     Stores internal data for the session.
        /// </summary>
        internal InternalDataStore Data { get; } = new InternalDataStore();

        /// <summary>
        ///     Gets or sets the user data.
        /// </summary>
        public object UserData { get; set; }

        /// <summary>
        ///     Override UpStreamEndPoint for this request; Local NIC via request is made
        /// </summary>
        public IPEndPoint UpStreamEndPoint { get; set; }

        /// <summary>
        ///     Headers passed with Connect.
        /// </summary>
        public ConnectRequest ConnectRequest { get; internal set; }

        /// <summary>
        ///     Web Request.
        /// </summary>
        public Request Request { get; }

        /// <summary>
        ///     Web Response.
        /// </summary>
        public Response Response { get; internal set; }

        /// <summary>
        ///     PID of the process that is created the current session when client is running in this machine
        ///     If client is remote then this will return
        /// </summary>
        public Lazy<int> ProcessId { get; internal set; }

        /// <summary>
        ///     Is Https?
        /// </summary>
        public bool IsHttps => Request.IsHttps;

        /// <summary>
        ///     Set the tcp connection to server used by this webclient
        /// </summary>
        /// <param name="serverConnection">Instance of <see cref="TcpServerConnection" /></param>
        internal void SetConnection(TcpServerConnection serverConnection)
        {
            serverConnection.LastAccess = DateTime.Now;
            Connection = serverConnection;
        }

        /// <summary>
        ///     Prepare and send the http(s) request
        /// </summary>
        /// <returns></returns>
        internal async Task SendRequest(bool enable100ContinueBehaviour, bool isTransparent,
            CancellationToken cancellationToken)
        {
            var upstreamProxy = Connection.UpStreamProxy;

            bool useUpstreamProxy = upstreamProxy != null && Connection.IsHttps == false;

            var writer = Connection.StreamWriter;

            // prepare the request & headers
            await writer.WriteLineAsync(Request.CreateRequestLine(Request.Method,
                useUpstreamProxy || isTransparent ? Request.OriginalUrl : Request.RequestUri.PathAndQuery,
                Request.HttpVersion), cancellationToken);

            var headerBuilder = new StringBuilder();
            
            // Send Authentication to Upstream proxy if needed
            if (!isTransparent && upstreamProxy != null
                               && Connection.IsHttps == false
                               && !string.IsNullOrEmpty(upstreamProxy.UserName)
                               && upstreamProxy.Password != null)
            {
                headerBuilder.Append($"{HttpHeader.ProxyConnectionKeepAlive}{ProxyConstants.NewLine}");
                headerBuilder.Append($"{HttpHeader.GetProxyAuthorizationHeader(upstreamProxy.UserName, upstreamProxy.Password)}{ProxyConstants.NewLine}");
            }

            // write request headers
            foreach (var header in Request.Headers)
            {
                if (isTransparent || header.Name != KnownHeaders.ProxyAuthorization)
                {
                    headerBuilder.Append($"{header}{ProxyConstants.NewLine}");
                }
            }

            headerBuilder.Append(ProxyConstants.NewLine);
            
            await writer.WriteAsync(headerBuilder.ToString(), cancellationToken);

            if (enable100ContinueBehaviour)
            {
                if (Request.ExpectContinue)
                {
                    string httpStatus;
                    try
                    {
                        httpStatus = await Connection.Stream.ReadLineAsync(cancellationToken);
                        if (httpStatus == null)
                        {
                            throw new ServerConnectionException("Server connection was closed.");
                        }
                    }
                    catch (Exception e) when (!(e is ServerConnectionException))
                    {
                        throw new ServerConnectionException("Server connection was closed.");
                    }

                    Response.ParseResponseLine(httpStatus, out _, out int responseStatusCode,
                        out string responseStatusDescription);

                    // find if server is willing for expect continue
                    if (responseStatusCode == (int)HttpStatusCode.Continue
                        && responseStatusDescription.EqualsIgnoreCase("continue"))
                    {
                        Request.Is100Continue = true;
                        await Connection.Stream.ReadLineAsync(cancellationToken);
                    }
                    else if (responseStatusCode == (int)HttpStatusCode.ExpectationFailed
                             && responseStatusDescription.EqualsIgnoreCase("expectation failed"))
                    {
                        Request.ExpectationFailed = true;
                        await Connection.Stream.ReadLineAsync(cancellationToken);
                    }
                }
            }
        }

        /// <summary>
        ///     Receive and parse the http response from server
        /// </summary>
        /// <returns></returns>
        internal async Task ReceiveResponse(CancellationToken cancellationToken)
        {
            // return if this is already read
            if (Response.StatusCode != 0)
            {
                return;
            }

            string httpStatus;
            try
            {
                httpStatus = await Connection.Stream.ReadLineAsync(cancellationToken);
                if (httpStatus == null)
                {
                    throw new ServerConnectionException("Server connection was closed.");
                }
            }
            catch (Exception e) when (!(e is ServerConnectionException))
            {
                throw new ServerConnectionException("Server connection was closed.");
            }

            if (httpStatus == string.Empty)
            {
                httpStatus = await Connection.Stream.ReadLineAsync(cancellationToken);
            }

            Response.ParseResponseLine(httpStatus, out var version, out int statusCode, out string statusDescription);

            Response.HttpVersion = version;
            Response.StatusCode = statusCode;
            Response.StatusDescription = statusDescription;

            // For HTTP 1.1 comptibility server may send expect-continue even if not asked for it in request
            if (Response.StatusCode == (int)HttpStatusCode.Continue
                && Response.StatusDescription.EqualsIgnoreCase("continue"))
            {
                // Read the next line after 100-continue 
                Response.Is100Continue = true;
                Response.StatusCode = 0;
                await Connection.Stream.ReadLineAsync(cancellationToken);

                // now receive response
                await ReceiveResponse(cancellationToken);
                return;
            }

            if (Response.StatusCode == (int)HttpStatusCode.ExpectationFailed
                && Response.StatusDescription.EqualsIgnoreCase("expectation failed"))
            {
                // read next line after expectation failed response
                Response.ExpectationFailed = true;
                Response.StatusCode = 0;
                await Connection.Stream.ReadLineAsync(cancellationToken);

                // now receive response 
                await ReceiveResponse(cancellationToken);
                return;
            }

            // Read the response headers in to unique and non-unique header collections
            await HeaderParser.ReadHeaders(Connection.Stream, Response.Headers, cancellationToken);
        }

        /// <summary>
        ///     Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        internal void FinishSession()
        {
            Connection = null;

            ConnectRequest?.FinishSession();
            Request?.FinishSession();
            Response?.FinishSession();

            Data.Clear();
            UserData = null;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Decompression;
using Titanium.Web.Proxy.Extensions;
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
    public class SessionEventArgs : EventArgs, IDisposable
    {
        /// <summary>
        /// Size of Buffers used by this object
        /// </summary>
        private readonly int bufferSize;

        /// <summary>
        /// Holds a reference to proxy response handler method
        /// </summary>
        private Func<SessionEventArgs, Task> httpResponseHandler;

        /// <summary>
        /// Backing field for corresponding public property
        /// </summary>
        private bool reRequest;

        /// <summary>
        /// Holds a reference to client
        /// </summary>
        internal ProxyClient ProxyClient { get; set; }

        internal bool HasMulipartEventSubscribers => MultipartRequestPartSent != null;

        /// <summary>
        /// Returns a unique Id for this request/response session
        /// same as RequestId of WebSession
        /// </summary>
        public Guid Id => WebSession.RequestId;

        /// <summary>
        /// Should we send the request again 
        /// </summary>
        public bool ReRequest
        {
            get => reRequest;
            set
            {
                if (WebSession.Response.StatusCode == 0)
                {
                    throw new Exception("Response status code is empty. Cannot request again a request " + "which was never send to server.");
                }

                reRequest = value;
            }
        }

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

        /// <summary>
        /// Occurs when multipart request part sent.
        /// </summary>
        public event EventHandler<MultipartRequestPartSentEventArgs> MultipartRequestPartSent;

        public ProxyEndPoint LocalEndPoint;

        /// <summary>
        /// Constructor to initialize the proxy
        /// </summary>
        internal SessionEventArgs(int bufferSize, 
            ProxyEndPoint endPoint,
            Func<SessionEventArgs, Task> httpResponseHandler)
        {
            this.bufferSize = bufferSize;
            this.httpResponseHandler = httpResponseHandler;

            ProxyClient = new ProxyClient();
            WebSession = new HttpWebClient(bufferSize);
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

        /// <summary>
        /// Read request body content as bytes[] for current session
        /// </summary>
        private async Task ReadRequestBody()
        {
            WebSession.Request.EnsureBodyAvailable(false);

            var request = WebSession.Request;

            //If not already read (not cached yet)
            if (!request.IsBodyRead)
            {
                //If chunked then its easy just read the whole body with the content length mentioned in the request header
                using (var bodyStream = new MemoryStream())
                {
                    var streamReader = ProxyClient.ClientStreamReader;

                    //For chunked request we need to read data as they arrive, until we reach a chunk end symbol
                    if (request.IsChunked)
                    {
                        await streamReader.CopyBytesToStreamChunked(bodyStream);
                    }
                    else
                    {
                        //If not chunked then its easy just read the whole body with the content length mentioned in the request header
                        if (request.ContentLength > 0)
                        {
                            //If not chunked then its easy just read the amount of bytes mentioned in content length header of response
                            await streamReader.CopyBytesToStream(bodyStream, request.ContentLength);
                        }
                        else if (request.HttpVersion.Major == 1 && request.HttpVersion.Minor == 0)
                        {
                            await streamReader.CopyBytesToStream(bodyStream, long.MaxValue);
                        }
                    }

                    request.Body = await GetDecompressedResponseBody(request.ContentEncoding, bodyStream.ToArray());
                }

                //Now set the flag to true
                //So that next time we can deliver body from cache
                request.IsBodyRead = true;
                var body = request.Body;
                OnDataSent(body, 0, body.Length);
            }
        }

        /// <summary>
        /// reinit response object
        /// </summary>
        internal async Task ClearResponse()
        {
            //siphon out the body
            await ReadResponseBody();
            WebSession.Response = new Response();
        }

        internal void OnDataSent(byte[] buffer, int offset, int count)
        {
            DataSent?.Invoke(this, new DataEventArgs(buffer, offset, count));
        }

        internal void OnDataReceived(byte[] buffer, int offset, int count)
        {
            DataReceived?.Invoke(this, new DataEventArgs(buffer, offset, count));
        }

        internal void OnMultipartRequestPartSent(string boundary, HeaderCollection headers)
        {
            MultipartRequestPartSent?.Invoke(this, new MultipartRequestPartSentEventArgs(boundary, headers));
        }

        /// <summary>
        /// Read response body as byte[] for current response
        /// </summary>
        private async Task ReadResponseBody()
        {
            if (!WebSession.Request.RequestLocked)
            {
                throw new Exception("You cannot read the response body before request is made to server.");
            }

            var response = WebSession.Response;

            //If not already read (not cached yet)
            if (!response.IsBodyRead)
            {
                if (response.HasBody)
                {
                    using (var bodyStream = new MemoryStream())
                    {
                        var streamReader = WebSession.ServerConnection.StreamReader;

                        //For chunked request we need to read data as they arrive, until we reach a chunk end symbol
                        if (response.IsChunked)
                        {
                            await streamReader.CopyBytesToStreamChunked(bodyStream);
                        }
                        else
                        {
                            //If not chunked then its easy just read the whole body with the content length mentioned in the request header
                            if (response.ContentLength > 0)
                            {
                                //If not chunked then its easy just read the amount of bytes mentioned in content length header of response
                                await streamReader.CopyBytesToStream(bodyStream, response.ContentLength);
                            }
                            else if (response.HttpVersion.Major == 1 && response.HttpVersion.Minor == 0 || 
                                     response.ContentLength == -1)
                            {
                                await streamReader.CopyBytesToStream(bodyStream, long.MaxValue);
                            }
                        }

                        response.Body = await GetDecompressedResponseBody(response.ContentEncoding, bodyStream.ToArray());
                    }
                }
                else
                {
                    response.Body = new byte[0];
                }

                //Now set the flag to true
                //So that next time we can deliver body from cache
                response.IsBodyRead = true;
                var body = response.Body;
                OnDataReceived(body, 0, body.Length);
            }
        }

        /// <summary>
        /// Gets the request body as bytes
        /// </summary>
        /// <returns></returns>
        public async Task<byte[]> GetRequestBody()
        {
            if (!WebSession.Request.IsBodyRead)
            {
                await ReadRequestBody();
            }

            return WebSession.Request.Body;
        }

        /// <summary>
        /// Gets the request body as string
        /// </summary>
        /// <returns></returns>
        public async Task<string> GetRequestBodyAsString()
        {
            if (!WebSession.Request.IsBodyRead)
            {
                await ReadRequestBody();
            }

            return WebSession.Request.BodyString;
        }

        /// <summary>
        /// Sets the request body
        /// </summary>
        /// <param name="body"></param>
        public async Task SetRequestBody(byte[] body)
        {
            if (WebSession.Request.RequestLocked)
            {
                throw new Exception("You cannot call this function after request is made to server.");
            }

            //syphon out the request body from client before setting the new body
            if (!WebSession.Request.IsBodyRead)
            {
                await ReadRequestBody();
            }

            WebSession.Request.Body = body;
            WebSession.Request.ContentLength = WebSession.Request.IsChunked ? -1 : body.Length;
        }

        /// <summary>
        /// Sets the body with the specified string
        /// </summary>
        /// <param name="body"></param>
        public async Task SetRequestBodyString(string body)
        {
            if (WebSession.Request.RequestLocked)
            {
                throw new Exception("You cannot call this function after request is made to server.");
            }

            //syphon out the request body from client before setting the new body
            if (!WebSession.Request.IsBodyRead)
            {
                await ReadRequestBody();
            }

            await SetRequestBody(WebSession.Request.Encoding.GetBytes(body));
        }

        /// <summary>
        /// Gets the response body as byte array
        /// </summary>
        /// <returns></returns>
        public async Task<byte[]> GetResponseBody()
        {
            if (!WebSession.Response.IsBodyRead)
            {
                await ReadResponseBody();
            }

            return WebSession.Response.Body;
        }

        /// <summary>
        /// Gets the response body as string
        /// </summary>
        /// <returns></returns>
        public async Task<string> GetResponseBodyAsString()
        {
            if (!WebSession.Response.IsBodyRead)
            {
                await ReadResponseBody();
            }

            return WebSession.Response.BodyString;
        }

        /// <summary>
        /// Set the response body bytes
        /// </summary>
        /// <param name="body"></param>
        public async Task SetResponseBody(byte[] body)
        {
            if (!WebSession.Request.RequestLocked)
            {
                throw new Exception("You cannot call this function before request is made to server.");
            }

            //syphon out the response body from server before setting the new body
            if (WebSession.Response.Body == null)
            {
                await GetResponseBody();
            }

            WebSession.Response.Body = body;

            //If there is a content length header update it
            if (WebSession.Response.IsChunked == false)
            {
                WebSession.Response.ContentLength = body.Length;
            }
            else
            {
                WebSession.Response.ContentLength = -1;
            }
        }

        /// <summary>
        /// Replace the response body with the specified string
        /// </summary>
        /// <param name="body"></param>
        public async Task SetResponseBodyString(string body)
        {
            if (!WebSession.Request.RequestLocked)
            {
                throw new Exception("You cannot call this function before request is made to server.");
            }

            //syphon out the response body from server before setting the new body
            if (!WebSession.Response.IsBodyRead)
            {
                await GetResponseBody();
            }

            var bodyBytes = WebSession.Response.Encoding.GetBytes(body);

            await SetResponseBody(bodyBytes);
        }

        private async Task<byte[]> GetDecompressedResponseBody(string encodingType, byte[] responseBodyStream)
        {
            var decompressionFactory = new DecompressionFactory();
            var decompressor = decompressionFactory.Create(encodingType);

            return await decompressor.Decompress(responseBodyStream, bufferSize);
        }

        /// <summary>
        /// Before request is made to server 
        /// Respond with the specified HTML string to client
        /// and ignore the request 
        /// </summary>
        /// <param name="html"></param>
        /// <param name="headers"></param>
        public async Task Ok(string html, Dictionary<string, HttpHeader> headers)
        {
            var response = new OkResponse();
            response.Headers.AddHeaders(headers);
            response.HttpVersion = WebSession.Request.HttpVersion;
            response.Body = response.Encoding.GetBytes(html ?? string.Empty);

            await Respond(response);

            WebSession.Request.CancelRequest = true;
        }

        /// <summary>
        /// Before request is made to server 
        /// Respond with the specified byte[] to client
        /// and ignore the request 
        /// </summary>
        /// <param name="result"></param>
        /// <param name="headers"></param>
        public async Task Ok(byte[] result, Dictionary<string, HttpHeader> headers = null)
        {
            var response = new OkResponse();
            response.Headers.AddHeaders(headers);
            response.HttpVersion = WebSession.Request.HttpVersion;
            response.Body = result;

            await Respond(response);
        }

        /// <summary>
        /// Before request is made to server 
        /// Respond with the specified HTML string to client
        /// and the specified status
        /// and ignore the request 
        /// </summary>
        /// <param name="html"></param>
        /// <param name="status"></param>
        /// <param name="headers"></param>
        /// <returns></returns>
        public async Task GenericResponse(string html, HttpStatusCode status, Dictionary<string, HttpHeader> headers = null)
        {
            var response = new GenericResponse(status);
            response.HttpVersion = WebSession.Request.HttpVersion;
            response.Headers.AddHeaders(headers);
            response.Body = response.Encoding.GetBytes(html ?? string.Empty);

            await Respond(response);
        }

        /// <summary>
        /// Before request is made to server
        /// Respond with the specified byte[] to client
        /// and the specified status
        /// and ignore the request
        /// </summary>
        /// <param name="result"></param>
        /// <param name="status"></param>
        /// <param name="headers"></param>
        /// <returns></returns>
        public async Task GenericResponse(byte[] result, HttpStatusCode status, Dictionary<string, HttpHeader> headers)
        {
            var response = new GenericResponse(status);
            response.HttpVersion = WebSession.Request.HttpVersion;
            response.Headers.AddHeaders(headers);
            response.Body = result;

            await Respond(response);
        }

        /// <summary>
        /// Redirect to URL.
        /// </summary>
        /// <param name="url"></param>
        /// <returns></returns>
        public async Task Redirect(string url)
        {
            var response = new RedirectResponse();
            response.HttpVersion = WebSession.Request.HttpVersion;
            response.Headers.AddHeader("Location", url);
            response.Body = new byte[0];

            await Respond(response);
        }

        /// a generic responder method 
        public async Task Respond(Response response)
        {
            if (WebSession.Request.RequestLocked)
            {
                throw new Exception("You cannot call this function after request is made to server.");
            }

            WebSession.Request.RequestLocked = true;

            response.ResponseLocked = true;
            response.IsBodyRead = true;

            WebSession.Response = response;

            await httpResponseHandler(this);

            WebSession.Request.CancelRequest = true;
        }

        /// <summary>
        /// implement any cleanup here
        /// </summary>
        public void Dispose()
        {
            httpResponseHandler = null;
            CustomUpStreamProxyUsed = null;

            DataSent = null;
            DataReceived = null;
            MultipartRequestPartSent = null;

            WebSession.FinishSession();
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using StreamExtended.Network;
using Titanium.Web.Proxy.Compression;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http.Responses;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.EventArguments
{
    /// <summary>
    /// Holds info related to a single proxy session (single request/response sequence).
    /// A proxy session is bounded to a single connection from client.
    /// A proxy session ends when client terminates connection to proxy
    /// or when server terminates connection from proxy.
    /// </summary>
    public class SessionEventArgs : SessionEventArgsBase
    {
        private static readonly byte[] emptyData = new byte[0];

        /// <summary>
        /// Backing field for corresponding public property
        /// </summary>
        private bool reRequest;

        /// <summary>
        /// Constructor to initialize the proxy
        /// </summary>
        internal SessionEventArgs(ProxyServer server, ProxyEndPoint endPoint,
            CancellationTokenSource cancellationTokenSource)
            : this(server, endPoint, null, cancellationTokenSource)
        {
        }

        protected SessionEventArgs(ProxyServer server, ProxyEndPoint endPoint,
            Request request, CancellationTokenSource cancellationTokenSource)
            : base(server, endPoint, cancellationTokenSource, request)
        {
        }

        private bool hasMulipartEventSubscribers => MultipartRequestPartSent != null;

        /// <summary>
        /// Should we send the request again ?
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
        /// Occurs when multipart request part sent.
        /// </summary>
        public event EventHandler<MultipartRequestPartSentEventArgs> MultipartRequestPartSent;

        private ICustomStreamReader getStreamReader(bool isRequest)
        {
            return isRequest ? ProxyClient.ClientStream : WebSession.ServerConnection.Stream;
        }

        private HttpWriter getStreamWriter(bool isRequest)
        {
            return isRequest ? (HttpWriter)ProxyClient.ClientStreamWriter : WebSession.ServerConnection.StreamWriter;
        }

        /// <summary>
        /// Read request body content as bytes[] for current session
        /// </summary>
        private async Task readRequestBodyAsync(CancellationToken cancellationToken)
        {
            WebSession.Request.EnsureBodyAvailable(false);

            var request = WebSession.Request;

            // If not already read (not cached yet)
            if (!request.IsBodyRead)
            {
                var body = await readBodyAsync(true, cancellationToken);
                request.Body = body;

                // Now set the flag to true
                // So that next time we can deliver body from cache
                request.IsBodyRead = true;
                OnDataSent(body, 0, body.Length);
            }
        }

        /// <summary>
        /// reinit response object
        /// </summary>
        internal async Task ClearResponse(CancellationToken cancellationToken)
        {
            // syphon out the response body from server
            await SyphonOutBodyAsync(false, cancellationToken);
            WebSession.Response = new Response();
        }

        internal void OnMultipartRequestPartSent(string boundary, HeaderCollection headers)
        {
            try
            {
                MultipartRequestPartSent?.Invoke(this, new MultipartRequestPartSentEventArgs(boundary, headers));
            }
            catch (Exception ex)
            {
                exceptionFunc(new Exception("Exception thrown in user event", ex));
            }
        }

        /// <summary>
        /// Read response body as byte[] for current response
        /// </summary>
        private async Task readResponseBodyAsync(CancellationToken cancellationToken)
        {
            if (!WebSession.Request.Locked)
            {
                throw new Exception("You cannot read the response body before request is made to server.");
            }

            var response = WebSession.Response;
            if (!response.HasBody)
            {
                return;
            }

            // If not already read (not cached yet)
            if (!response.IsBodyRead)
            {
                var body = await readBodyAsync(false, cancellationToken);
                response.Body = body;

                // Now set the flag to true
                // So that next time we can deliver body from cache
                response.IsBodyRead = true;
                OnDataReceived(body, 0, body.Length);
            }
        }

        private async Task<byte[]> readBodyAsync(bool isRequest, CancellationToken cancellationToken)
        {
            using (var bodyStream = new MemoryStream())
            {
                var writer = new HttpWriter(bodyStream, bufferPool, bufferSize);

                if (isRequest)
                {
                    await CopyRequestBodyAsync(writer, TransformationMode.Uncompress, cancellationToken);
                }
                else
                {
                    await CopyResponseBodyAsync(writer, TransformationMode.Uncompress, cancellationToken);
                }

                return bodyStream.ToArray();
            }
        }

        /// <summary>
        ///     Syphon out any left over data in given request/response from backing tcp connection.
        ///     When user modifies the response/request we need to do this to reuse tcp connections.
        /// </summary>
        /// <param name="isRequest"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        internal async Task SyphonOutBodyAsync(bool isRequest, CancellationToken cancellationToken)
        {
            var requestResponse = isRequest ? (RequestResponseBase)WebSession.Request : WebSession.Response;
            if (requestResponse.OriginalIsBodyRead || !requestResponse.OriginalHasBody)
            {
                return;
            }

            using (var bodyStream = new MemoryStream())
            {
                var writer = new HttpWriter(bodyStream, bufferPool, bufferSize);
                await copyBodyAsync(isRequest, true, writer, TransformationMode.None, null, cancellationToken);
            }
        }

        /// <summary>
        ///  This is called when the request is PUT/POST/PATCH to read the body
        /// </summary>
        /// <returns></returns>
        internal async Task CopyRequestBodyAsync(HttpWriter writer, TransformationMode transformation, CancellationToken cancellationToken)
        {
            var request = WebSession.Request;

            long contentLength = request.ContentLength;

            // send the request body bytes to server
            if (contentLength > 0 && hasMulipartEventSubscribers && request.IsMultipartFormData)
            {
                var reader = getStreamReader(true);
                string boundary = HttpHelper.GetBoundaryFromContentType(request.ContentType);

                using (var copyStream = new CopyStream(reader, writer, bufferPool, bufferSize))
                {
                    while (contentLength > copyStream.ReadBytes)
                    {
                        long read = await readUntilBoundaryAsync(copyStream, contentLength, boundary, cancellationToken);
                        if (read == 0)
                        {
                            break;
                        }

                        if (contentLength > copyStream.ReadBytes)
                        {
                            var headers = new HeaderCollection();
                            await HeaderParser.ReadHeaders(copyStream, headers, cancellationToken);
                            OnMultipartRequestPartSent(boundary, headers);
                        }
                    }

                    await copyStream.FlushAsync(cancellationToken);
                }
            }
            else
            {
                await copyBodyAsync(true, false, writer, transformation, OnDataSent, cancellationToken);
            }
        }

        internal async Task CopyResponseBodyAsync(HttpWriter writer, TransformationMode transformation, CancellationToken cancellationToken)
        {
            await copyBodyAsync(false, false, writer, transformation, OnDataReceived, cancellationToken);
        }

        private async Task copyBodyAsync(bool isRequest, bool useOriginalHeaderValues, HttpWriter writer, TransformationMode transformation, Action<byte[], int, int> onCopy, CancellationToken cancellationToken)
        {
            var stream = getStreamReader(isRequest);

            var requestResponse = isRequest ? (RequestResponseBase)WebSession.Request : WebSession.Response;

            bool isChunked = useOriginalHeaderValues? requestResponse.OriginalIsChunked : requestResponse.IsChunked;
            long contentLength = useOriginalHeaderValues ? requestResponse.OriginalContentLength : requestResponse.ContentLength;

            if (transformation == TransformationMode.None)
            {
                await writer.CopyBodyAsync(stream, isChunked, contentLength, onCopy, cancellationToken);
                return;
            }

            LimitedStream limitedStream;
            Stream decompressStream = null;

            string contentEncoding = useOriginalHeaderValues ? requestResponse.OriginalContentEncoding : requestResponse.ContentEncoding;

            Stream s = limitedStream = new LimitedStream(stream, bufferPool, isChunked, contentLength);

            if (transformation == TransformationMode.Uncompress && contentEncoding != null)
            {
                s = decompressStream = DecompressionFactory.Create(contentEncoding, s);
            }

            try
            {
                using (var bufStream = new CustomBufferedStream(s, bufferPool, bufferSize, true))
                {
                    await writer.CopyBodyAsync(bufStream, false, -1, onCopy, cancellationToken);
                }
            }
            finally
            {
                decompressStream?.Dispose();

                await limitedStream.Finish();
                limitedStream.Dispose();
            }
        }

        /// <summary>
        /// Read a line from the byte stream
        /// </summary>
        /// <returns></returns>
        private async Task<long> readUntilBoundaryAsync(ICustomStreamReader reader, long totalBytesToRead, string boundary, CancellationToken cancellationToken)
        {
            int bufferDataLength = 0;

            var buffer = bufferPool.GetBuffer(bufferSize);
            try
            {
                int boundaryLength = boundary.Length + 4;
                long bytesRead = 0;

                while (bytesRead < totalBytesToRead && (reader.DataAvailable || await reader.FillBufferAsync(cancellationToken)))
                {
                    byte newChar = reader.ReadByteFromBuffer();
                    buffer[bufferDataLength] = newChar;

                    bufferDataLength++;
                    bytesRead++;

                    if (bufferDataLength >= boundaryLength)
                    {
                        int startIdx = bufferDataLength - boundaryLength;
                        if (buffer[startIdx] == '-' && buffer[startIdx + 1] == '-')
                        {
                            startIdx += 2;
                            bool ok = true;
                            for (int i = 0; i < boundary.Length; i++)
                            {
                                if (buffer[startIdx + i] != boundary[i])
                                {
                                    ok = false;
                                    break;
                                }
                            }

                            if (ok)
                            {
                                break;
                            }
                        }
                    }

                    if (bufferDataLength == buffer.Length)
                    {
                        // boundary is not longer than 70 bytes according to the specification, so keeping the last 100 (minimum 74) bytes is enough
                        const int bytesToKeep = 100;
                        Buffer.BlockCopy(buffer, buffer.Length - bytesToKeep, buffer, 0, bytesToKeep);
                        bufferDataLength = bytesToKeep;
                    }
                }

                return bytesRead;
            }
            finally
            {
                bufferPool.ReturnBuffer(buffer);
            }
        }

        /// <summary>
        /// Gets the request body as bytes.
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token for this async task.</param>
        /// <returns>The body as bytes.</returns>
        public async Task<byte[]> GetRequestBody(CancellationToken cancellationToken = default)
        {
            if (!WebSession.Request.IsBodyRead)
            {
                await readRequestBodyAsync(cancellationToken);
            }

            return WebSession.Request.Body;
        }

        /// <summary>
        /// Gets the request body as string.
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token for this async task.</param>
        /// <returns>The body as string.</returns>
        public async Task<string> GetRequestBodyAsString(CancellationToken cancellationToken = default)
        {
            if (!WebSession.Request.IsBodyRead)
            {
                await readRequestBodyAsync(cancellationToken);
            }

            return WebSession.Request.BodyString;
        }

        /// <summary>
        /// Sets the request body.
        /// </summary>
        /// <param name="body">The request body bytes.</param>
        public void SetRequestBody(byte[] body)
        {
            var request = WebSession.Request;
            if (request.Locked)
            {
                throw new Exception("You cannot call this function after request is made to server.");
            }

            request.Body = body;
        }

        /// <summary>
        /// Sets the body with the specified string.
        /// </summary>
        /// <param name="body">The request body string to set.</param>
        public void SetRequestBodyString(string body)
        {
            if (WebSession.Request.Locked)
            {
                throw new Exception("You cannot call this function after request is made to server.");
            }

            SetRequestBody(WebSession.Request.Encoding.GetBytes(body));
        }


        /// <summary>
        /// Gets the response body as bytes.
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token for this async task.</param>
        /// <returns>The resulting bytes.</returns>
        public async Task<byte[]> GetResponseBody(CancellationToken cancellationToken = default)
        {
            if (!WebSession.Response.IsBodyRead)
            {
                await readResponseBodyAsync(cancellationToken);
            }

            return WebSession.Response.Body;
        }

        /// <summary>
        /// Gets the response body as string.
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token for this async task.</param>
        /// <returns>The string body.</returns>
        public async Task<string> GetResponseBodyAsString(CancellationToken cancellationToken = default)
        {
            if (!WebSession.Response.IsBodyRead)
            {
                await readResponseBodyAsync(cancellationToken);
            }

            return WebSession.Response.BodyString;
        }

        /// <summary>
        /// Set the response body bytes.
        /// </summary>
        /// <param name="body">The body bytes to set.</param>
        public void SetResponseBody(byte[] body)
        {
            if (!WebSession.Request.Locked)
            {
                throw new Exception("You cannot call this function before request is made to server.");
            }

            var response = WebSession.Response;
            response.Body = body;
        }

        /// <summary>
        /// Replace the response body with the specified string.
        /// </summary>
        /// <param name="body">The body string to set.</param>
        public void SetResponseBodyString(string body)
        {
            if (!WebSession.Request.Locked)
            {
                throw new Exception("You cannot call this function before request is made to server.");
            }

            var bodyBytes = WebSession.Response.Encoding.GetBytes(body);

            SetResponseBody(bodyBytes);
        }

        /// <summary>
        /// Before request is made to server respond with the specified HTML string to client
        /// and ignore the request. 
        /// </summary>
        /// <param name="html">HTML content to sent.</param>
        /// <param name="headers">HTTP response headers.</param>
        /// <param name="closeServerConnection">Close the server connection used by request if any?</param>
        public void Ok(string html, Dictionary<string, HttpHeader> headers = null,
            bool closeServerConnection = false)
        {
            var response = new OkResponse();
            if (headers != null)
            {
                response.Headers.AddHeaders(headers);
            }

            response.HttpVersion = WebSession.Request.HttpVersion;
            response.Body = response.Encoding.GetBytes(html ?? string.Empty);

            Respond(response, closeServerConnection);
        }

        /// <summary>
        /// Before request is made to server respond with the specified byte[] to client
        /// and ignore the request. 
        /// </summary>
        /// <param name="result">The html content bytes.</param>
        /// <param name="headers">The HTTP headers.</param>
        /// <param name="closeServerConnection">Close the server connection used by request if any?</param>
        public void Ok(byte[] result, Dictionary<string, HttpHeader> headers = null,
            bool closeServerConnection = false)
        {
            var response = new OkResponse();
            response.Headers.AddHeaders(headers);
            response.HttpVersion = WebSession.Request.HttpVersion;
            response.Body = result;

            Respond(response, closeServerConnection);
        }

        /// <summary>
        /// Before request is made to server 
        /// respond with the specified HTML string and the specified status to client.
        /// And then ignore the request. 
        /// </summary>
        /// <param name="html">The html content.</param>
        /// <param name="status">The HTTP status code.</param>
        /// <param name="headers">The HTTP headers.</param>
        /// <param name="closeServerConnection">Close the server connection used by request if any?</param>
        public void GenericResponse(string html, HttpStatusCode status,
            Dictionary<string, HttpHeader> headers = null, bool closeServerConnection = false)
        {
            var response = new GenericResponse(status);
            response.HttpVersion = WebSession.Request.HttpVersion;
            response.Headers.AddHeaders(headers);
            response.Body = response.Encoding.GetBytes(html ?? string.Empty);

            Respond(response, closeServerConnection);
        }

        /// <summary>
        /// Before request is made to server respond with the specified byte[],
        /// the specified status  to client. And then ignore the request.
        /// </summary>
        /// <param name="result">The bytes to sent.</param>
        /// <param name="status">The HTTP status code.</param>
        /// <param name="headers">The HTTP headers.</param>
        /// <param name="closeServerConnection">Close the server connection used by request if any?</param>
        public void GenericResponse(byte[] result, HttpStatusCode status,
            Dictionary<string, HttpHeader> headers, bool closeServerConnection = false)
        {
            var response = new GenericResponse(status);
            response.HttpVersion = WebSession.Request.HttpVersion;
            response.Headers.AddHeaders(headers);
            response.Body = result;

            Respond(response, closeServerConnection);
        }

        /// <summary>
        /// Redirect to provided URL.
        /// </summary>
        /// <param name="url">The URL to redirect.</param>
        /// <param name="closeServerConnection">Close the server connection used by request if any?</param>
        public void Redirect(string url, bool closeServerConnection = false)
        {
            var response = new RedirectResponse();
            response.HttpVersion = WebSession.Request.HttpVersion;
            response.Headers.AddHeader(KnownHeaders.Location, url);
            response.Body = emptyData;

            Respond(response, closeServerConnection);
        }

        /// <summary>
        /// Respond with given response object to client.
        /// </summary>
        /// <param name="response">The response object.</param>
        /// <param name="closeServerConnection">Close the server connection used by request if any?</param>
        public void Respond(Response response, bool closeServerConnection = false)
        {
            //request already send/ready to be sent.
            if (WebSession.Request.Locked)
            {
                //response already received from server and ready to be sent to client.
                if (WebSession.Response.Locked)
                {
                    throw new Exception("You cannot call this function after response is sent to the client.");
                }

                //cleanup original response.
                if (closeServerConnection)
                {
                    //no need to cleanup original connection.
                    //it will be closed any way.
                    TerminateServerConnection();
                }

                response.SetOriginalHeaders(WebSession.Response);

                //response already received from server but not yet ready to sent to client.         
                WebSession.Response = response;
                WebSession.Response.Locked = true;
            }
            //request not yet sent/not yet ready to be sent.
            else
            {
                WebSession.Request.Locked = true;
                WebSession.Request.CancelRequest = true;
              
                //set new response.
                WebSession.Response = response;
                WebSession.Response.Locked = true;
            }

        }

        /// <summary>
        ///     Terminate the connection to server at the end of this HTTP request/response session.
        /// </summary>
        public void TerminateServerConnection()
        {
            WebSession.CloseServerConnection = true;
        }

        /// <summary>
        /// Implement any cleanup here
        /// </summary>
        public override void Dispose()
        {
            MultipartRequestPartSent = null;
            base.Dispose();
        }
    }
}

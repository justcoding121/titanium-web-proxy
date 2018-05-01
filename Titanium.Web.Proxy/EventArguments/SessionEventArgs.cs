using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using StreamExtended.Helpers;
using StreamExtended.Network;
using Titanium.Web.Proxy.Compression;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http.Responses;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;

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
        internal SessionEventArgs(int bufferSize, ProxyEndPoint endPoint,
            CancellationTokenSource cancellationTokenSource, ExceptionHandler exceptionFunc)
            : this(bufferSize, endPoint, null, cancellationTokenSource, exceptionFunc)
        {
        }

        protected SessionEventArgs(int bufferSize, ProxyEndPoint endPoint,
            Request request, CancellationTokenSource cancellationTokenSource, ExceptionHandler exceptionFunc)
            : base(bufferSize, endPoint, cancellationTokenSource, request, exceptionFunc)
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

        private ICustomStreamReader GetStreamReader(bool isRequest)
        {
            return isRequest ? ProxyClient.ClientStream : WebSession.ServerConnection.Stream;
        }

        private HttpWriter GetStreamWriter(bool isRequest)
        {
            return isRequest ? (HttpWriter)ProxyClient.ClientStreamWriter : WebSession.ServerConnection.StreamWriter;
        }

        /// <summary>
        /// Read request body content as bytes[] for current session
        /// </summary>
        private async Task ReadRequestBodyAsync(CancellationToken cancellationToken)
        {
            WebSession.Request.EnsureBodyAvailable(false);

            var request = WebSession.Request;

            // If not already read (not cached yet)
            if (!request.IsBodyRead)
            {
                var body = await ReadBodyAsync(true, cancellationToken);
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
                ExceptionFunc(new Exception("Exception thrown in user event", ex));
            }
        }

        /// <summary>
        /// Read response body as byte[] for current response
        /// </summary>
        private async Task ReadResponseBodyAsync(CancellationToken cancellationToken)
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
                var body = await ReadBodyAsync(false, cancellationToken);
                response.Body = body;

                // Now set the flag to true
                // So that next time we can deliver body from cache
                response.IsBodyRead = true;
                OnDataReceived(body, 0, body.Length);
            }
        }

        private async Task<byte[]> ReadBodyAsync(bool isRequest, CancellationToken cancellationToken)
        {
            using (var bodyStream = new MemoryStream())
            {
                var writer = new HttpWriter(bodyStream, BufferSize);

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

        internal async Task SyphonOutBodyAsync(bool isRequest, CancellationToken cancellationToken)
        {
            var requestResponse = isRequest ? (RequestResponseBase)WebSession.Request : WebSession.Response;
            if (requestResponse.IsBodyRead || !requestResponse.OriginalHasBody)
            {
                return;
            }

            using (var bodyStream = new MemoryStream())
            {
                var writer = new HttpWriter(bodyStream, BufferSize);
                await CopyBodyAsync(isRequest, writer, TransformationMode.None, null, cancellationToken);
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
                var reader = GetStreamReader(true);
                string boundary = HttpHelper.GetBoundaryFromContentType(request.ContentType);

                using (var copyStream = new CopyStream(reader, writer, BufferSize))
                {
                    while (contentLength > copyStream.ReadBytes)
                    {
                        long read = await ReadUntilBoundaryAsync(copyStream, contentLength, boundary, cancellationToken);
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
                await CopyBodyAsync(true, writer, transformation, OnDataSent, cancellationToken);
            }
        }

        internal async Task CopyResponseBodyAsync(HttpWriter writer, TransformationMode transformation, CancellationToken cancellationToken)
        {
            await CopyBodyAsync(false, writer, transformation, OnDataReceived, cancellationToken);
        }

        private async Task CopyBodyAsync(bool isRequest, HttpWriter writer, TransformationMode transformation, Action<byte[], int, int> onCopy, CancellationToken cancellationToken)
        {
            var stream = GetStreamReader(isRequest);

            var requestResponse = isRequest ? (RequestResponseBase)WebSession.Request : WebSession.Response;

            bool isChunked = requestResponse.IsChunked;
            long contentLength = requestResponse.ContentLength;
            if (transformation == TransformationMode.None)
            {
                await writer.CopyBodyAsync(stream, isChunked, contentLength, onCopy, cancellationToken);
                return;
            }

            LimitedStream limitedStream;
            Stream decompressStream = null;

            string contentEncoding = requestResponse.ContentEncoding;

            Stream s = limitedStream = new LimitedStream(stream, isChunked, contentLength);

            if (transformation == TransformationMode.Uncompress && contentEncoding != null)
            {
                s = decompressStream = DecompressionFactory.Create(contentEncoding, s);
            }

            try
            {
                using (var bufStream = new CustomBufferedStream(s, BufferSize, true))
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
        private async Task<long> ReadUntilBoundaryAsync(ICustomStreamReader reader, long totalBytesToRead, string boundary, CancellationToken cancellationToken)
        {
            int bufferDataLength = 0;

            var buffer = BufferPool.GetBuffer(BufferSize);
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
                BufferPool.ReturnBuffer(buffer);
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
                await ReadRequestBodyAsync(cancellationToken);
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
                await ReadRequestBodyAsync(cancellationToken);
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
                await ReadResponseBodyAsync(cancellationToken);
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
                await ReadResponseBodyAsync(cancellationToken);
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
        public void Ok(string html, Dictionary<string, HttpHeader> headers = null)
        {
            var response = new OkResponse();
            if (headers != null)
            {
                response.Headers.AddHeaders(headers);
            }

            response.HttpVersion = WebSession.Request.HttpVersion;
            response.Body = response.Encoding.GetBytes(html ?? string.Empty);

            Respond(response);
        }

        /// <summary>
        /// Before request is made to server respond with the specified byte[] to client
        /// and ignore the request. 
        /// </summary>
        /// <param name="result">The html content bytes.</param>
        /// <param name="headers">The HTTP headers.</param>
        public void Ok(byte[] result, Dictionary<string, HttpHeader> headers = null)
        {
            var response = new OkResponse();
            response.Headers.AddHeaders(headers);
            response.HttpVersion = WebSession.Request.HttpVersion;
            response.Body = result;

            Respond(response);
        }

        /// <summary>
        /// Before request is made to server 
        /// respond with the specified HTML string and the specified status to client.
        /// And then ignore the request. 
        /// </summary>
        /// <param name="html">The html content.</param>
        /// <param name="status">The HTTP status code.</param>
        /// <param name="headers">The HTTP headers.</param>
        /// <returns></returns>
        public void GenericResponse(string html, HttpStatusCode status, Dictionary<string, HttpHeader> headers = null)
        {
            var response = new GenericResponse(status);
            response.HttpVersion = WebSession.Request.HttpVersion;
            response.Headers.AddHeaders(headers);
            response.Body = response.Encoding.GetBytes(html ?? string.Empty);

            Respond(response);
        }

        /// <summary>
        /// Before request is made to server respond with the specified byte[],
        /// the specified status  to client. And then ignore the request.
        /// </summary>
        /// <param name="result">The bytes to sent.</param>
        /// <param name="status">The HTTP status code.</param>
        /// <param name="headers">The HTTP headers.</param>
        /// <returns></returns>
        public void GenericResponse(byte[] result, HttpStatusCode status, Dictionary<string, HttpHeader> headers)
        {
            var response = new GenericResponse(status);
            response.HttpVersion = WebSession.Request.HttpVersion;
            response.Headers.AddHeaders(headers);
            response.Body = result;

            Respond(response);
        }

        /// <summary>
        /// Redirect to provided URL.
        /// </summary>
        /// <param name="url">The URL to redirect.</param>
        /// <returns></returns>
        public void Redirect(string url)
        {
            var response = new RedirectResponse();
            response.HttpVersion = WebSession.Request.HttpVersion;
            response.Headers.AddHeader(KnownHeaders.Location, url);
            response.Body = emptyData;

            Respond(response);
        }

        /// <summary>
      /// Respond with given response object to client.
      /// </summary>
      /// <param name="response">The response object.</param>
        public void Respond(Response response)
        {
            if (WebSession.Request.Locked)
            {
                if (WebSession.Response.Locked)
                {
                    throw new Exception("You cannot call this function after response is sent to the client.");
                }

                response.Locked = true;
                response.TerminateResponse = WebSession.Response.TerminateResponse;
                WebSession.Response = response;
            }
            else
            {
                WebSession.Request.Locked = true;

                response.Locked = true;
                WebSession.Response = response;

                WebSession.Request.CancelRequest = true;
            }
        }

        /// <summary>
        /// Terminate the connection to server.
        /// </summary>
        public void TerminateServerConnection()
        {
            WebSession.Response.TerminateResponse = true;
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

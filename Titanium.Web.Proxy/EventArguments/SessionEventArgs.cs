using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
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
    public class SessionEventArgs : EventArgs, IDisposable
    {
        private static readonly byte[] emptyData = new byte[0];

        /// <summary>
        /// Size of Buffers used by this object
        /// </summary>
        private readonly int bufferSize;

        private readonly ExceptionHandler exceptionFunc;

        /// <summary>
        /// Backing field for corresponding public property
        /// </summary>
        private bool reRequest;

        /// <summary>
        /// Holds a reference to client
        /// </summary>
        internal ProxyClient ProxyClient { get; }

        private bool hasMulipartEventSubscribers => MultipartRequestPartSent != null;

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

        public ProxyEndPoint LocalEndPoint { get; }

        public bool IsTransparent => LocalEndPoint is TransparentProxyEndPoint;

        public Exception Exception { get; internal set; }

        /// <summary>
        /// Constructor to initialize the proxy
        /// </summary>
        internal SessionEventArgs(int bufferSize,
            ProxyEndPoint endPoint,
            ExceptionHandler exceptionFunc)
        {
            this.bufferSize = bufferSize;
            this.exceptionFunc = exceptionFunc;

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
        private async Task ReadRequestBodyAsync()
        {
            WebSession.Request.EnsureBodyAvailable(false);

            var request = WebSession.Request;

            //If not already read (not cached yet)
            if (!request.IsBodyRead)
            {
                var body = await ReadBodyAsync(ProxyClient.ClientStreamReader, true);
                request.Body = body;

                //Now set the flag to true
                //So that next time we can deliver body from cache
                request.IsBodyRead = true;
                OnDataSent(body, 0, body.Length);
            }
        }

        /// <summary>
        /// reinit response object
        /// </summary>
        internal async Task ClearResponse()
        {
            //siphon out the body
            await ReadResponseBodyAsync();
            WebSession.Response = new Response();
        }

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
        private async Task ReadResponseBodyAsync()
        {
            if (!WebSession.Request.RequestLocked)
            {
                throw new Exception("You cannot read the response body before request is made to server.");
            }

            var response = WebSession.Response;
            if (!response.HasBody)
            {
                return;
            }

            //If not already read (not cached yet)
            if (!response.IsBodyRead)
            {
                var body = await ReadBodyAsync(WebSession.ServerConnection.StreamReader, false);
                response.Body = body;

                //Now set the flag to true
                //So that next time we can deliver body from cache
                response.IsBodyRead = true;
                OnDataReceived(body, 0, body.Length);
            }
        }

        private async Task<byte[]> ReadBodyAsync(CustomBinaryReader reader, bool isRequest)
        {
            using (var bodyStream = new MemoryStream())
            {
                var writer = new HttpWriter(bodyStream, bufferSize);
                if (isRequest)
                {
                    await CopyRequestBodyAsync(writer, TransformationMode.Uncompress);
                }
                else
                {
                    await CopyResponseBodyAsync(writer, TransformationMode.Uncompress);
                }

                return bodyStream.ToArray();
            }
        }

        /// <summary>
        ///  This is called when the request is PUT/POST/PATCH to read the body
        /// </summary>
        /// <returns></returns>
        internal async Task CopyRequestBodyAsync(HttpWriter writer, TransformationMode transformation)
        {
            // End the operation
            var request = WebSession.Request;
            var reader = ProxyClient.ClientStreamReader;

            long contentLength = request.ContentLength;

            //send the request body bytes to server
            if (contentLength > 0 && hasMulipartEventSubscribers && request.IsMultipartFormData)
            {
                string boundary = HttpHelper.GetBoundaryFromContentType(request.ContentType);

                using (var copyStream = new CopyStream(reader, writer, bufferSize))
                using (var copyStreamReader = new CustomBinaryReader(copyStream, bufferSize))
                {
                    while (contentLength > copyStream.ReadBytes)
                    {
                        long read = await ReadUntilBoundaryAsync(copyStreamReader, contentLength, boundary);
                        if (read == 0)
                        {
                            break;
                        }

                        if (contentLength > copyStream.ReadBytes)
                        {
                            var headers = new HeaderCollection();
                            await HeaderParser.ReadHeaders(copyStreamReader, headers);
                            OnMultipartRequestPartSent(boundary, headers);
                        }
                    }

                    await copyStream.FlushAsync();
                }
            }
            else
            {
                await CopyBodyAsync(ProxyClient.ClientStream, reader, writer,
                    request.IsChunked, transformation, request.ContentEncoding, contentLength, OnDataSent);
            }
        }

        private async Task CopyBodyAsync(CustomBufferedStream stream, CustomBinaryReader reader, HttpWriter writer,
            bool isChunked, TransformationMode transformation, string contentEncoding, long contentLength,
            Action<byte[], int, int> onCopy)
        {
            bool newReader = false;

            Stream s = stream;
            ChunkedStream chunkedStream = null;
            Stream decompressStream = null;

            if (isChunked && transformation != TransformationMode.None)
            {
                s = chunkedStream = new ChunkedStream(stream, reader);
                isChunked = false;
                newReader = true;
            }

            if (transformation == TransformationMode.Uncompress)
            {
                s = decompressStream = DecompressionFactory.Instance.Create(contentEncoding).GetStream(s);
                newReader = true;
            }

            try
            {
                if (newReader)
                {
                    var bufStream = new CustomBufferedStream(s, bufferSize);
                    s = bufStream;
                    reader = new CustomBinaryReader(bufStream, bufferSize);
                }

                await writer.CopyBodyAsync(reader, isChunked, contentLength, onCopy);
            }
            finally
            {
                if (newReader)
                {
                    reader?.Dispose();
                    decompressStream?.Dispose();
                    if (chunkedStream != null)
                    {
                        await chunkedStream.Finish();
                        chunkedStream.Dispose();
                    }
                }
            }
        }

        internal async Task CopyResponseBodyAsync(HttpWriter writer, TransformationMode transformation)
        {
            var response = WebSession.Response;
            var reader = WebSession.ServerConnection.StreamReader;

            await CopyBodyAsync(WebSession.ServerConnection.Stream, reader, writer,
                response.IsChunked, transformation, response.ContentEncoding, response.ContentLength, OnDataReceived);
        }

        /// <summary>
        /// Read a line from the byte stream
        /// </summary>
        /// <returns></returns>
        private async Task<long> ReadUntilBoundaryAsync(CustomBinaryReader reader, long totalBytesToRead, string boundary)
        {
            int bufferDataLength = 0;

            var buffer = BufferPool.GetBuffer(bufferSize);
            try
            {
                int boundaryLength = boundary.Length + 4;
                long bytesRead = 0;

                while (bytesRead < totalBytesToRead && (reader.DataAvailable || await reader.FillBufferAsync()))
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
                        //boundary is not longer than 70 bytes according to the specification, so keeping the last 100 (minimum 74) bytes is enough
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
        /// Gets the request body as bytes
        /// </summary>
        /// <returns></returns>
        public async Task<byte[]> GetRequestBody()
        {
            if (!WebSession.Request.IsBodyRead)
            {
                await ReadRequestBodyAsync();
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
                await ReadRequestBodyAsync();
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
                await ReadRequestBodyAsync();
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
                await ReadRequestBodyAsync();
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
                await ReadResponseBodyAsync();
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
                await ReadResponseBodyAsync();
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

        /// <summary>
        /// Before request is made to server 
        /// Respond with the specified HTML string to client
        /// and ignore the request 
        /// </summary>
        /// <param name="html"></param>
        /// <param name="headers"></param>
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
        /// Before request is made to server 
        /// Respond with the specified byte[] to client
        /// and ignore the request 
        /// </summary>
        /// <param name="result"></param>
        /// <param name="headers"></param>
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
        /// Respond with the specified HTML string to client
        /// and the specified status
        /// and ignore the request 
        /// </summary>
        /// <param name="html"></param>
        /// <param name="status"></param>
        /// <param name="headers"></param>
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
        /// Before request is made to server
        /// Respond with the specified byte[] to client
        /// and the specified status
        /// and ignore the request
        /// </summary>
        /// <param name="result"></param>
        /// <param name="status"></param>
        /// <param name="headers"></param>
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
        /// Redirect to URL.
        /// </summary>
        /// <param name="url"></param>
        /// <returns></returns>
        public void Redirect(string url)
        {
            var response = new RedirectResponse();
            response.HttpVersion = WebSession.Request.HttpVersion;
            response.Headers.AddHeader(KnownHeaders.Location, url);
            response.Body = emptyData;

            Respond(response);
        }

        /// a generic responder method 
        public void Respond(Response response)
        {
            if (WebSession.Request.RequestLocked)
            {
                throw new Exception("You cannot call this function after request is made to server.");
            }

            WebSession.Request.RequestLocked = true;

            response.ResponseLocked = true;
            response.IsBodyRead = true;

            WebSession.Response = response;

            WebSession.Request.CancelRequest = true;
        }

        /// <summary>
        /// implement any cleanup here
        /// </summary>
        public void Dispose()
        {
            CustomUpStreamProxyUsed = null;

            DataSent = null;
            DataReceived = null;
            MultipartRequestPartSent = null;
            Exception = null;

            WebSession.FinishSession();
        }
    }
}

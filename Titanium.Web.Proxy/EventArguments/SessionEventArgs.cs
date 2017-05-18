using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Decompression;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
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
        /// Holds a reference to client
        /// </summary>
        internal ProxyClient ProxyClient { get; set; }

        /// <summary>
        /// Returns a unique Id for this request/response session
        /// same as RequestId of WebSession
        /// </summary>
        public Guid Id => WebSession.RequestId;

        /// <summary>
        /// Should we send a rerequest 
        /// </summary>
        public bool ReRequest { get; set; }

        /// <summary>
        /// Does this session uses SSL
        /// </summary>
        public bool IsHttps => WebSession.Request.RequestUri.Scheme == Uri.UriSchemeHttps;

        /// <summary>
        /// Client End Point.
        /// </summary>
        public IPEndPoint ClientEndPoint => (IPEndPoint)ProxyClient.TcpClient.Client.RemoteEndPoint;

        /// <summary>
        /// A web session corresponding to a single request/response sequence
        /// within a proxy connection
        /// </summary>
        public HttpWebClient WebSession { get; set; }

        /// <summary>
        /// Are we using a custom upstream HTTP proxy?
        /// </summary>
        public ExternalProxy CustomUpStreamHttpProxyUsed { get; set; }

        /// <summary>
        /// Are we using a custom upstream HTTPS proxy?
        /// </summary>
        public ExternalProxy CustomUpStreamHttpsProxyUsed { get; set; }

        /// <summary>
        /// Constructor to initialize the proxy
        /// </summary>
        internal SessionEventArgs(int bufferSize, Func<SessionEventArgs, Task> httpResponseHandler)
        {
            this.bufferSize = bufferSize;
            this.httpResponseHandler = httpResponseHandler;

            ProxyClient = new ProxyClient();
            WebSession = new HttpWebClient();
        }

        /// <summary>
        /// Read request body content as bytes[] for current session
        /// </summary>
        private async Task ReadRequestBody()
        {
            //GET request don't have a request body to read
            var method = WebSession.Request.Method.ToUpper();
            if (method != "POST" && method != "PUT" && method != "PATCH")
            {
                throw new BodyNotFoundException("Request don't have a body. " +
                                                "Please verify that this request is a Http POST/PUT/PATCH and request " +
                                                "content length is greater than zero before accessing the body.");
            }

            //Caching check
            if (WebSession.Request.RequestBody == null)
            {
                //If chunked then its easy just read the whole body with the content length mentioned in the request header
                using (var requestBodyStream = new MemoryStream())
                {
                    //For chunked request we need to read data as they arrive, until we reach a chunk end symbol
                    if (WebSession.Request.IsChunked)
                    {
                        await ProxyClient.ClientStreamReader.CopyBytesToStreamChunked(requestBodyStream);
                    }
                    else
                    {
                        //If not chunked then its easy just read the whole body with the content length mentioned in the request header
                        if (WebSession.Request.ContentLength > 0)
                        {
                            //If not chunked then its easy just read the amount of bytes mentioned in content length header of response
                            await ProxyClient.ClientStreamReader.CopyBytesToStream(bufferSize, requestBodyStream,
                                WebSession.Request.ContentLength);
                        }
                        else if (WebSession.Request.HttpVersion.Major == 1 && WebSession.Request.HttpVersion.Minor == 0)
                        {
                            await WebSession.ServerConnection.StreamReader.CopyBytesToStream(bufferSize, requestBodyStream, long.MaxValue);
                        }
                    }
                    WebSession.Request.RequestBody = await GetDecompressedResponseBody(WebSession.Request.ContentEncoding,
                        requestBodyStream.ToArray());
                }

                //Now set the flag to true
                //So that next time we can deliver body from cache
                WebSession.Request.RequestBodyRead = true;
            }
        }

        /// <summary>
        /// Read response body as byte[] for current response
        /// </summary>
        private async Task ReadResponseBody()
        {
            //If not already read (not cached yet)
            if (WebSession.Response.ResponseBody == null)
            {
                using (var responseBodyStream = new MemoryStream())
                {
                    //If chuncked the read chunk by chunk until we hit chunk end symbol
                    if (WebSession.Response.IsChunked)
                    {
                        await WebSession.ServerConnection.StreamReader.CopyBytesToStreamChunked(responseBodyStream);
                    }
                    else
                    {
                        if (WebSession.Response.ContentLength > 0)
                        {
                            //If not chunked then its easy just read the amount of bytes mentioned in content length header of response
                            await WebSession.ServerConnection.StreamReader.CopyBytesToStream(bufferSize, responseBodyStream,
                                WebSession.Response.ContentLength);
                        }
                        else if (WebSession.Response.HttpVersion.Major == 1 && WebSession.Response.HttpVersion.Minor == 0 || WebSession.Response.ContentLength == -1)
                        {
                            await WebSession.ServerConnection.StreamReader.CopyBytesToStream(bufferSize, responseBodyStream, long.MaxValue);
                        }
                    }

                    WebSession.Response.ResponseBody = await GetDecompressedResponseBody(WebSession.Response.ContentEncoding,
                        responseBodyStream.ToArray());
                }
                //set this to true for caching
                WebSession.Response.ResponseBodyRead = true;
            }
        }

        /// <summary>
        /// Gets the request body as bytes
        /// </summary>
        /// <returns></returns>
        public async Task<byte[]> GetRequestBody()
        {
            if (!WebSession.Request.RequestBodyRead)
            {
                if (WebSession.Request.RequestLocked)
                {
                    throw new Exception("You cannot call this function after request is made to server.");
                }

                await ReadRequestBody();
            }
            return WebSession.Request.RequestBody;
        }

        /// <summary>
        /// Gets the request body as string
        /// </summary>
        /// <returns></returns>
        public async Task<string> GetRequestBodyAsString()
        {
            if (!WebSession.Request.RequestBodyRead)
            {
                if (WebSession.Request.RequestLocked)
                {
                    throw new Exception("You cannot call this function after request is made to server.");
                }

                await ReadRequestBody();
            }
            //Use the encoding specified in request to decode the byte[] data to string
            return WebSession.Request.RequestBodyString ?? (WebSession.Request.RequestBodyString = WebSession.Request.Encoding.GetString(WebSession.Request.RequestBody));
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
            if (!WebSession.Request.RequestBodyRead)
            {
                await ReadRequestBody();
            }

            WebSession.Request.RequestBody = body;

            if (WebSession.Request.IsChunked == false)
            {
                WebSession.Request.ContentLength = body.Length;
            }
            else
            {
                WebSession.Request.ContentLength = -1;
            }
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
            if (!WebSession.Request.RequestBodyRead)
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
            if (!WebSession.Request.RequestLocked)
            {
                throw new Exception("You cannot call this function before request is made to server.");
            }

            await ReadResponseBody();
            return WebSession.Response.ResponseBody;
        }

        /// <summary>
        /// Gets the response body as string
        /// </summary>
        /// <returns></returns>
        public async Task<string> GetResponseBodyAsString()
        {
            if (!WebSession.Request.RequestLocked)
            {
                throw new Exception("You cannot call this function before request is made to server.");
            }

            await GetResponseBody();

            return WebSession.Response.ResponseBodyString ??
                   (WebSession.Response.ResponseBodyString = WebSession.Response.Encoding.GetString(WebSession.Response.ResponseBody));
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
            if (WebSession.Response.ResponseBody == null)
            {
                await GetResponseBody();
            }

            WebSession.Response.ResponseBody = body;

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
            if (WebSession.Response.ResponseBody == null)
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
        public async Task Ok(string html)
        {
            await Ok(html, null);
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
            if (WebSession.Request.RequestLocked)
            {
                throw new Exception("You cannot call this function after request is made to server.");
            }

            if (html == null)
            {
                html = string.Empty;
            }

            var result = Encoding.Default.GetBytes(html);

            await Ok(result, headers);
        }

        /// <summary>
        /// Before request is made to server 
        /// Respond with the specified byte[] to client
        /// and ignore the request 
        /// </summary>
        /// <param name="result"></param>
        public async Task Ok(byte[] result)
        {
            await Ok(result, null);
        }

        /// <summary>
        /// Before request is made to server 
        /// Respond with the specified byte[] to client
        /// and ignore the request 
        /// </summary>
        /// <param name="result"></param>
        /// <param name="headers"></param>
        public async Task Ok(byte[] result, Dictionary<string, HttpHeader> headers)
        {
            var response = new OkResponse();

            if (headers != null && headers.Count > 0)
            {
                response.ResponseHeaders = headers;
            }

            response.HttpVersion = WebSession.Request.HttpVersion;
            response.ResponseBody = result;

            await Respond(response);

            WebSession.Request.CancelRequest = true;
        }

        /// <summary>
        /// Before request is made to server 
        /// Respond with the specified HTML string to client
        /// and ignore the request 
        /// </summary>
        /// <param name="html"></param>
        /// <param name="status"></param>
        public async Task GenericResponse(string html, HttpStatusCode status)
        {
            await GenericResponse(html, null, status);
        }

        /// <summary>
        /// Before request is made to server 
        /// Respond with the specified HTML string to client
        /// and the specified status
        /// and ignore the request 
        /// </summary>
        /// <param name="html"></param>
        /// <param name="headers"></param>
        /// <param name="status"></param>
        public async Task GenericResponse(string html, Dictionary<string, HttpHeader> headers, HttpStatusCode status)
        {
            if (WebSession.Request.RequestLocked)
            {
                throw new Exception("You cannot call this function after request is made to server.");
            }

            if (html == null)
            {
                html = string.Empty;
            }

            var result = Encoding.Default.GetBytes(html);

            await GenericResponse(result, headers, status);
        }

        /// <summary>
        /// Before request is made to server
        /// Respond with the specified byte[] to client
        /// and the specified status
        /// and ignore the request
        /// </summary>
        /// <param name="result"></param>
        /// <param name="headers"></param>
        /// <param name="status"></param>
        /// <returns></returns>
        public async Task GenericResponse(byte[] result, Dictionary<string, HttpHeader> headers, HttpStatusCode status)
        {
            var response = new GenericResponse(status);

            if (headers != null && headers.Count > 0)
            {
                response.ResponseHeaders = headers;
            }

            response.HttpVersion = WebSession.Request.HttpVersion;

            response.ResponseBody = result;

            await Respond(response);

            WebSession.Request.CancelRequest = true;
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
            response.ResponseHeaders.Add("Location", new HttpHeader("Location", url));
            response.ResponseBody = Encoding.ASCII.GetBytes(string.Empty);

            await Respond(response);

            WebSession.Request.CancelRequest = true;
        }

        /// a generic responder method 
        public async Task Respond(Response response)
        {
            WebSession.Request.RequestLocked = true;

            response.ResponseLocked = true;
            response.ResponseBodyRead = true;

            WebSession.Response = response;

            await httpResponseHandler(this);
        }

        /// <summary>
        /// implement any cleanup here
        /// </summary>
        public void Dispose()
        {
            httpResponseHandler = null;
            CustomUpStreamHttpProxyUsed = null;
            CustomUpStreamHttpsProxyUsed = null;

            WebSession.Dispose();
        }
    }
}

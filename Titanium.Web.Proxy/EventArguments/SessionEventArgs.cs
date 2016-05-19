using System;
using System.IO;
using System.Text;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Decompression;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http.Responses;
using Titanium.Web.Proxy.Extensions;

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
        /// Constructor to initialize the proxy
        /// </summary>
        internal SessionEventArgs()
        {
            Client = new ProxyClient();
            WebSession = new HttpWebSession();
        }

        /// <summary>
        /// Holds a reference to server connection
        /// </summary>
        internal ProxyClient Client { get; set; }

        /// <summary>
        /// Does this session uses SSL
        /// </summary>
        public bool IsHttps { get; internal set; }

        /// <summary>
        /// A web session corresponding to a single request/response sequence
        /// within a proxy connection
        /// </summary>
        public HttpWebSession WebSession { get; set; }


        /// <summary>
        /// implement any cleanup here
        /// </summary>
        public void Dispose()
        {

        }

        /// <summary>
        /// Read request body content as bytes[] for current session
        /// </summary>
        private void ReadRequestBody()
        {
            //GET request don't have a request body to read
            if ((WebSession.Request.Method.ToUpper() != "POST" && WebSession.Request.Method.ToUpper() != "PUT"))
            {
                throw new BodyNotFoundException("Request don't have a body." +
                                                "Please verify that this request is a Http POST/PUT and request content length is greater than zero before accessing the body.");
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
                        this.Client.ClientStreamReader.CopyBytesToStreamChunked(requestBodyStream);  
                    }
                    else
                    {
                        //If not chunked then its easy just read the whole body with the content length mentioned in the request header
                        if (WebSession.Request.ContentLength > 0)
                        {
                            //If not chunked then its easy just read the amount of bytes mentioned in content length header of response
                            this.Client.ClientStreamReader.CopyBytesToStream(requestBodyStream, WebSession.Request.ContentLength);

                        }
                    }
                    WebSession.Request.RequestBody = GetDecompressedResponseBody(WebSession.Request.ContentEncoding, requestBodyStream.ToArray());
                }
            }

            //Now set the flag to true
            //So that next time we can deliver body from cache
            WebSession.Request.RequestBodyRead = true;
        }

        /// <summary>
        /// Read response body as byte[] for current response
        /// </summary>
        private void ReadResponseBody()
        {
            //If not already read (not cached yet)
            if (WebSession.Response.ResponseBody == null)
            {
                using (var responseBodyStream = new MemoryStream())
                {
                    //If chuncked the read chunk by chunk until we hit chunk end symbol
                    if (WebSession.Response.IsChunked)
                    {
                        WebSession.ProxyClient.ServerStreamReader.CopyBytesToStreamChunked(responseBodyStream);    
                    }
                    else
                    {
                        if (WebSession.Response.ContentLength > 0)
                        {
                            //If not chunked then its easy just read the amount of bytes mentioned in content length header of response
                            WebSession.ProxyClient.ServerStreamReader.CopyBytesToStream(responseBodyStream, WebSession.Response.ContentLength);

                        }
                    }

                    WebSession.Response.ResponseBody = GetDecompressedResponseBody(WebSession.Response.ContentEncoding, responseBodyStream.ToArray());

                }
                //set this to true for caching
                WebSession.Response.ResponseBodyRead = true;
            }
        }

        /// <summary>
        /// Gets the request body as bytes
        /// </summary>
        /// <returns></returns>
        public byte[] GetRequestBody()
        {
            if (WebSession.Request.RequestLocked)
                throw new Exception("You cannot call this function after request is made to server.");

            ReadRequestBody();
            return WebSession.Request.RequestBody;
        }
        /// <summary>
        /// Gets the request body as string
        /// </summary>
        /// <returns></returns>
        public string GetRequestBodyAsString()
        {
            if (WebSession.Request.RequestLocked)
                throw new Exception("You cannot call this function after request is made to server.");


            ReadRequestBody();

            //Use the encoding specified in request to decode the byte[] data to string
            return WebSession.Request.RequestBodyString ?? (WebSession.Request.RequestBodyString = WebSession.Request.Encoding.GetString(WebSession.Request.RequestBody));
        }

        /// <summary>
        /// Sets the request body
        /// </summary>
        /// <param name="body"></param>
        public void SetRequestBody(byte[] body)
        {
            if (WebSession.Request.RequestLocked)
                throw new Exception("You cannot call this function after request is made to server.");

            //syphon out the request body from client before setting the new body
            if (!WebSession.Request.RequestBodyRead)
            {
                ReadRequestBody();
            }

            WebSession.Request.RequestBody = body;
            WebSession.Request.RequestBodyRead = true;
        }

        /// <summary>
        /// Sets the body with the specified string
        /// </summary>
        /// <param name="body"></param>
        public void SetRequestBodyString(string body)
        {
            if (WebSession.Request.RequestLocked)
                throw new Exception("You cannot call this function after request is made to server.");

            //syphon out the request body from client before setting the new body
            if (!WebSession.Request.RequestBodyRead)
            {
                ReadRequestBody();
            }

            WebSession.Request.RequestBody = WebSession.Request.Encoding.GetBytes(body);

            //If there is a content length header update it
            if (!WebSession.Request.IsChunked)
                WebSession.Request.ContentLength = body.Length;

            WebSession.Request.RequestBodyRead = true;
        }

        /// <summary>
        /// Gets the response body as byte array
        /// </summary>
        /// <returns></returns>
        public byte[] GetResponseBody()
        {
            if (!WebSession.Request.RequestLocked)
                throw new Exception("You cannot call this function before request is made to server.");

            ReadResponseBody();
            return WebSession.Response.ResponseBody;
        }

        /// <summary>
        /// Gets the response body as string
        /// </summary>
        /// <returns></returns>
        public string GetResponseBodyAsString()
        {
            if (!WebSession.Request.RequestLocked)
                throw new Exception("You cannot call this function before request is made to server.");

            GetResponseBody();

            return WebSession.Response.ResponseBodyString ?? (WebSession.Response.ResponseBodyString = WebSession.Response.Encoding.GetString(WebSession.Response.ResponseBody));
        }

        /// <summary>
        /// Set the response body bytes
        /// </summary>
        /// <param name="body"></param>
        public void SetResponseBody(byte[] body)
        {
            if (!WebSession.Request.RequestLocked)
                throw new Exception("You cannot call this function before request is made to server.");

            //syphon out the response body from server before setting the new body
            if (WebSession.Response.ResponseBody == null)
            {
                GetResponseBody();
            }

            WebSession.Response.ResponseBody = body;

            //If there is a content length header update it
            if (!WebSession.Response.IsChunked)
                WebSession.Response.ContentLength = body.Length;

        }

        /// <summary>
        /// Replace the response body with the specified string
        /// </summary>
        /// <param name="body"></param>
        public void SetResponseBodyString(string body)
        {
            if (!WebSession.Request.RequestLocked)
                throw new Exception("You cannot call this function before request is made to server.");

            //syphon out the response body from server before setting the new body
            if (WebSession.Response.ResponseBody == null)
            {
                GetResponseBody();
            }

            var bodyBytes = WebSession.Response.Encoding.GetBytes(body);
            SetResponseBody(bodyBytes);
        }

        private byte[] GetDecompressedResponseBody(string encodingType, byte[] responseBodyStream)
        {
            var decompressionFactory = new DecompressionFactory();
            var decompressor = decompressionFactory.Create(encodingType);

            return decompressor.Decompress(responseBodyStream);
        }


        /// <summary>
        /// Before request is made to server 
        /// Respond with the specified HTML string to client
        /// and ignore the request 
        /// </summary>
        /// <param name="html"></param>
        public void Ok(string html)
        {
            if (WebSession.Request.RequestLocked)
                throw new Exception("You cannot call this function after request is made to server.");

            if (html == null)
                html = string.Empty;

            var result = Encoding.Default.GetBytes(html);

            Ok(result);
        }

        /// <summary>
        /// Before request is made to server 
        /// Respond with the specified byte[] to client
        /// and ignore the request 
        /// </summary>
        /// <param name="body"></param>
        public void Ok(byte[] result)
        {
            var response = new OkResponse();

            response.HttpVersion = WebSession.Request.HttpVersion;
            response.ResponseBody = result;

            Respond(response);

            WebSession.Request.CancelRequest = true;
        }

        public void Redirect(string url)
        {
            var response = new RedirectResponse();

            response.HttpVersion = WebSession.Request.HttpVersion;
            response.ResponseHeaders.Add(new Models.HttpHeader("Location", url));
            response.ResponseBody = Encoding.ASCII.GetBytes(string.Empty);

            Respond(response);

            WebSession.Request.CancelRequest = true;
        }

        /// a generic responder method 
        public void Respond(Response response)
        {
            WebSession.Request.RequestLocked = true;

            response.ResponseLocked = true;
            response.ResponseBodyRead = true;

            WebSession.Response = response;

            ProxyServer.HandleHttpSessionResponse(this);
        }

    }
}
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Responses;
using Titanium.Web.Proxy.Decompression;

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
            ProxySession = new HttpWebSession();
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
        public HttpWebSession ProxySession { get; set; }

        /// <summary>
        /// A shortcut to get the request content length
        /// </summary>
        public int RequestContentLength
        {
            get
            {
                return ProxySession.Request.ContentLength;
            }
        }

        /// <summary>
        /// A shortcut to get the request Method (GET/POST/PUT etc)
        /// </summary>
        public string RequestMethod
        {
            get { return ProxySession.Request.Method; }
        }

        /// <summary>
        /// A shortcut to get the response status code (200 OK, 404 etc)
        /// </summary>
        public string ResponseStatusCode
        {
            get { return ProxySession.Response.ResponseStatusCode; }
        }

        /// <summary>
        /// A shortcut to get the response content type
        /// </summary>
        public string ResponseContentType
        {
            get
            {
                return ProxySession.Response.ContentType;
            }
        }

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
            if ((ProxySession.Request.Method.ToUpper() != "POST" && ProxySession.Request.Method.ToUpper() != "PUT"))
            {
                throw new BodyNotFoundException("Request don't have a body." +
                                                "Please verify that this request is a Http POST/PUT and request content length is greater than zero before accessing the body.");
            }

            //Caching check
            if (ProxySession.Request.RequestBody == null)
            {
                var isChunked = false;
                string requestContentEncoding = null;

                //get compression method (gzip, zlib etc)
                if (ProxySession.Request.RequestHeaders.Any(x => x.Name.ToLower() == "content-encoding"))
                {
                    requestContentEncoding = ProxySession.Request.RequestHeaders.First(x => x.Name.ToLower() == "content-encoding").Value;
                }

                //check if the request have chunked body (body send chunck by chunck without a fixed length)
                if (ProxySession.Request.RequestHeaders.Any(x => x.Name.ToLower() == "transfer-encoding"))
                {
                    var transferEncoding =
                        ProxySession.Request.RequestHeaders.First(x => x.Name.ToLower() == "transfer-encoding").Value.ToLower();
                    if (transferEncoding.Contains("chunked"))
                    {
                        isChunked = true;
                    }
                }

                //If not chunked then its easy just read the whole body with the content length mentioned in the request header
                if (requestContentEncoding == null && !isChunked)
                    ProxySession.Request.RequestBody = this.Client.ClientStreamReader.ReadBytes(RequestContentLength);
                else
                {
                    using (var requestBodyStream = new MemoryStream())
                    {
                        //For chunked request we need to read data as they arrive, until we reach a chunk end symbol
                        if (isChunked)
                        {
                            while (true)
                            {
                                var chuchkHead = this.Client.ClientStreamReader.ReadLine();
                                var chunkSize = int.Parse(chuchkHead, NumberStyles.HexNumber);

                                if (chunkSize != 0)
                                {
                                    var buffer = this.Client.ClientStreamReader.ReadBytes(chunkSize);
                                    requestBodyStream.Write(buffer, 0, buffer.Length);
                                    //chunk trail
                                    this.Client.ClientStreamReader.ReadLine();
                                }
                                else
                                {
                                    //chunk end
                                    this.Client.ClientStreamReader.ReadLine();
                                    break;
                                }
                            }
                        }

                        try
                        {
                            ProxySession.Request.RequestBody = GetDecompressedResponseBody(requestContentEncoding, requestBodyStream.ToArray());
                        }
                        catch
                        {
                            //if decompression fails, just assign the body stream as it it
                            //Not a safe option
                            ProxySession.Request.RequestBody = requestBodyStream.ToArray();
                        }
                    }
                }
            }
            //Now set the flag to true
            //So that next time we can deliver body from cache
            ProxySession.Request.RequestBodyRead = true;
        }

        /// <summary>
        /// Read response body as byte[] for current response
        /// </summary>
        private void ReadResponseBody()
        {
            //If not already read (not cached yet)
            if (ProxySession.Response.ResponseBody == null)
            {
                using (var responseBodyStream = new MemoryStream())
                {
                    //If chuncked the read chunk by chunk until we hit chunk end symbol
                    if (ProxySession.Response.IsChunked)
                    {
                        while (true)
                        {
                            var chuchkHead = ProxySession.ProxyClient.ServerStreamReader.ReadLine();
                            var chunkSize = int.Parse(chuchkHead, NumberStyles.HexNumber);

                            if (chunkSize != 0)
                            {
                                var buffer = ProxySession.ProxyClient.ServerStreamReader.ReadBytes(chunkSize);
                                responseBodyStream.Write(buffer, 0, buffer.Length);
                                //chunk trail
                                ProxySession.ProxyClient.ServerStreamReader.ReadLine();
                            }
                            else
                            {
                                //chuck end
                                ProxySession.ProxyClient.ServerStreamReader.ReadLine();
                                break;
                            }
                        }
                    }
                    else
                    {
                        //If not chunked then its easy just read the amount of bytes mentioned in content length header of response
                        var buffer = ProxySession.ProxyClient.ServerStreamReader.ReadBytes(ProxySession.Response.ContentLength);
                        responseBodyStream.Write(buffer, 0, buffer.Length);
                    }

                    ProxySession.Response.ResponseBody = GetDecompressedResponseBody(ProxySession.Response.ContentEncoding, responseBodyStream.ToArray());

                }
                //set this to true for caching
                ProxySession.Response.ResponseBodyRead = true;
            }
        }

        /// <summary>
        /// Gets the request body as bytes
        /// </summary>
        /// <returns></returns>
        public byte[] GetRequestBody()
        {
            if (ProxySession.Request.RequestLocked) throw new Exception("You cannot call this function after request is made to server.");

            ReadRequestBody();
            return ProxySession.Request.RequestBody;
        }
        /// <summary>
        /// Gets the request body as string
        /// </summary>
        /// <returns></returns>
        public string GetRequestBodyAsString()
        {
            if (ProxySession.Request.RequestLocked) throw new Exception("You cannot call this function after request is made to server.");


            ReadRequestBody();

            //Use the encoding specified in request to decode the byte[] data to string
            return ProxySession.Request.RequestBodyString ?? (ProxySession.Request.RequestBodyString = ProxySession.Request.Encoding.GetString(ProxySession.Request.RequestBody));
        }

        /// <summary>
        /// Sets the request body
        /// </summary>
        /// <param name="body"></param>
        public void SetRequestBody(byte[] body)
        {
            if (ProxySession.Request.RequestLocked) throw new Exception("You cannot call this function after request is made to server.");

            //syphon out the request body from client before setting the new body
            if (!ProxySession.Request.RequestBodyRead)
            {
                ReadRequestBody();
            }

            ProxySession.Request.RequestBody = body;
            ProxySession.Request.RequestBodyRead = true;
        }

        /// <summary>
        /// Sets the body with the specified string
        /// </summary>
        /// <param name="body"></param>
        public void SetRequestBodyString(string body)
        {
            if (ProxySession.Request.RequestLocked) throw new Exception("You cannot call this function after request is made to server.");

            //syphon out the request body from client before setting the new body
            if (!ProxySession.Request.RequestBodyRead)
            {
                ReadRequestBody();
            }

            ProxySession.Request.RequestBody = ProxySession.Request.Encoding.GetBytes(body);
            ProxySession.Request.RequestBodyRead = true;
        }

        /// <summary>
        /// Gets the response body as byte array
        /// </summary>
        /// <returns></returns>
        public byte[] GetResponseBody()
        {
            if (!ProxySession.Request.RequestLocked) throw new Exception("You cannot call this function before request is made to server.");

            ReadResponseBody();
            return ProxySession.Response.ResponseBody;
        }

        /// <summary>
        /// Gets the response body as string
        /// </summary>
        /// <returns></returns>
        public string GetResponseBodyAsString()
        {
            if (!ProxySession.Request.RequestLocked) throw new Exception("You cannot call this function before request is made to server.");

            GetResponseBody();

            return ProxySession.Response.ResponseBodyString ?? (ProxySession.Response.ResponseBodyString = ProxySession.Response.Encoding.GetString(ProxySession.Response.ResponseBody));
        }

        /// <summary>
        /// Set the response body bytes
        /// </summary>
        /// <param name="body"></param>
        public void SetResponseBody(byte[] body)
        {
            if (!ProxySession.Request.RequestLocked) throw new Exception("You cannot call this function before request is made to server.");

            //syphon out the response body from server before setting the new body
            if (ProxySession.Response.ResponseBody == null)
            {
                GetResponseBody();
            }

            ProxySession.Response.ResponseBody = body;
        }

        /// <summary>
        /// Replace the response body with the specified string
        /// </summary>
        /// <param name="body"></param>
        public void SetResponseBodyString(string body)
        {
            if (!ProxySession.Request.RequestLocked) throw new Exception("You cannot call this function before request is made to server.");

            //syphon out the response body from server before setting the new body
            if (ProxySession.Response.ResponseBody == null)
            {
                GetResponseBody();
            }

            var bodyBytes = ProxySession.Response.Encoding.GetBytes(body);
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
            if (ProxySession.Request.RequestLocked) throw new Exception("You cannot call this function after request is made to server.");

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

            response.HttpVersion = ProxySession.Request.HttpVersion;
            response.ResponseBody = result;

            Respond(response);

            ProxySession.Request.CancelRequest = true;
        }

        public void Redirect(string url)
        {
            var response = new RedirectResponse();

            response.HttpVersion = ProxySession.Request.HttpVersion;
            response.ResponseHeaders.Add(new Models.HttpHeader("Location", url));
            response.ResponseBody = Encoding.ASCII.GetBytes(string.Empty);

            Respond(response);

            ProxySession.Request.CancelRequest = true;
        }

        /// a generic responder method 
        public void Respond(Response response)
        {
            ProxySession.Request.RequestLocked = true;

            response.ResponseLocked = true;
            response.ResponseBodyRead = true;

            ProxySession.Response = response;

            ProxyServer.HandleHttpSessionResponse(this);
        }
     
    }
}
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Models;

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
        readonly int _bufferSize;

        /// <summary>
        /// Constructor to initialize the proxy
        /// </summary>
        internal SessionEventArgs(int bufferSize)
        {
            _bufferSize = bufferSize;
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
                            //decompress
                            switch (requestContentEncoding)
                            {
                                case "gzip":
                                    ProxySession.Request.RequestBody = CompressionHelper.DecompressGzip(requestBodyStream.ToArray());
                                    break;
                                case "deflate":
                                    ProxySession.Request.RequestBody = CompressionHelper.DecompressDeflate(requestBodyStream);
                                    break;
                                case "zlib":
                                    ProxySession.Request.RequestBody = CompressionHelper.DecompressZlib(requestBodyStream);
                                    break;
                                default:
                                    ProxySession.Request.RequestBody = requestBodyStream.ToArray();
                                    break;
                            }
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
                    //decompress
                    switch (ProxySession.Response.ContentEncoding)
                    {
                        case "gzip":
                            ProxySession.Response.ResponseBody = CompressionHelper.DecompressGzip(responseBodyStream.ToArray());
                            break;
                        case "deflate":
                            ProxySession.Response.ResponseBody = CompressionHelper.DecompressDeflate(responseBodyStream);
                            break;
                        case "zlib":
                            ProxySession.Response.ResponseBody = CompressionHelper.DecompressZlib(responseBodyStream);
                            break;
                        default:
                            ProxySession.Response.ResponseBody = responseBodyStream.ToArray();
                            break;
                    }
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


        /// <summary>
        /// Before request is made to server 
        /// Respond with the specified HTML string to client
        /// and ignore the request 
        /// Marking as obsolete, need to comeup with a generic responder method in future
        /// </summary>
        /// <param name="html"></param>
       // [Obsolete]
        public void Ok(string html)
        {
            if (ProxySession.Request.RequestLocked) throw new Exception("You cannot call this function after request is made to server.");

            if (html == null)
                html = string.Empty;

            var result = Encoding.Default.GetBytes(html);

            var connectStreamWriter = new StreamWriter(this.Client.ClientStream);
            connectStreamWriter.WriteLine(string.Format("{0} {1} {2}", ProxySession.Request.HttpVersion, 200, "Ok"));
            connectStreamWriter.WriteLine("Timestamp: {0}", DateTime.Now);
            connectStreamWriter.WriteLine("content-length: " + result.Length);
            connectStreamWriter.WriteLine("Cache-Control: no-cache, no-store, must-revalidate");
            connectStreamWriter.WriteLine("Pragma: no-cache");
            connectStreamWriter.WriteLine("Expires: 0");

            connectStreamWriter.WriteLine(ProxySession.Request.IsAlive ? "Connection: Keep-Alive" : "Connection: close");

            connectStreamWriter.WriteLine();
            connectStreamWriter.Flush();

            this.Client.ClientStream.Write(result, 0, result.Length);

            ProxySession.Request.CancelRequest = true;
        }
    }
}
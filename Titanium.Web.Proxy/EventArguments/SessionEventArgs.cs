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
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.EventArguments
{
    public class SessionEventArgs : EventArgs, IDisposable
    {
        readonly int _bufferSize;

        internal SessionEventArgs(int bufferSize)
        {
            _bufferSize = bufferSize;
        }

        internal TcpClient Client { get; set; }
        internal Stream ClientStream { get; set; }
        internal CustomBinaryReader ClientStreamReader { get; set; }
        internal StreamWriter ClientStreamWriter { get; set; }


        public bool IsHttps { get; internal set; }
        public string RequestUrl { get; internal set; }
        public string RequestHostname { get; internal set; }

        public int ClientPort { get; internal set; }
        public IPAddress ClientIpAddress { get; internal set; }

        internal Encoding RequestEncoding { get; set; }
        internal Version RequestHttpVersion { get; set; }
        internal bool RequestIsAlive { get; set; }
        internal bool CancelRequest { get; set; }
        internal byte[] RequestBody { get; set; }
        internal string RequestBodyString { get; set; }
        internal bool RequestBodyRead { get; set; }
        public List<HttpHeader> RequestHeaders { get; internal set; }
        internal bool RequestLocked { get; set; }
        internal HttpWebRequest ProxyRequest { get; set; }

        internal Encoding ResponseEncoding { get; set; }
        internal Stream ResponseStream { get; set; }
        internal byte[] ResponseBody { get; set; }
        internal string ResponseBodyString { get; set; }
        internal bool ResponseBodyRead { get; set; }
        public List<HttpHeader> ResponseHeaders { get; internal set; }
        internal bool ResponseLocked { get; set; }
        internal HttpWebResponse ServerResponse { get; set; }

        public int RequestContentLength
        {
            get
            {
                if (RequestHeaders.All(x => x.Name.ToLower() != "content-length")) return -1;
                int contentLen;
                int.TryParse(RequestHeaders.First(x => x.Name.ToLower() == "content-length").Value, out contentLen);
                if (contentLen != 0)
                    return contentLen;
                return -1;
            }
        }

        public string RequestMethod
        {
            get { return ProxyRequest.Method; }
        }


        public HttpStatusCode ResponseStatusCode
        {
            get { return ServerResponse.StatusCode; }
        }

        public string ResponseContentType
        {
            get
            {
                return ResponseHeaders.Any(x => x.Name.ToLower() == "content-type")
                    ? ResponseHeaders.First(x => x.Name.ToLower() == "content-type").Value
                    : null;
            }
        }

        public void Dispose()
        {
            if (ProxyRequest != null)
                ProxyRequest.Abort();

            if (ResponseStream != null)
                ResponseStream.Dispose();

            if (ServerResponse != null)
                ServerResponse.Close();
        }

        private void ReadRequestBody()
        {
            if ((ProxyRequest.Method.ToUpper() != "POST" && ProxyRequest.Method.ToUpper() != "PUT"))
            {
                throw new BodyNotFoundException("Request don't have a body." +
                                                "Please verify that this request is a Http POST/PUT and request content length is greater than zero before accessing the body.");
            }

            if (RequestBody == null)
            {
                var isChunked = false;
                string requestContentEncoding = null;


                if (RequestHeaders.Any(x => x.Name.ToLower() == "content-encoding"))
                {
                    requestContentEncoding = RequestHeaders.First(x => x.Name.ToLower() == "content-encoding").Value;
                }

                if (RequestHeaders.Any(x => x.Name.ToLower() == "transfer-encoding"))
                {
                    var transferEncoding =
                        RequestHeaders.First(x => x.Name.ToLower() == "transfer-encoding").Value.ToLower();
                    if (transferEncoding.Contains("chunked"))
                    {
                        isChunked = true;
                    }
                }


                if (requestContentEncoding == null && !isChunked)
                    RequestBody = ClientStreamReader.ReadBytes(RequestContentLength);
                else
                {
                    using (var requestBodyStream = new MemoryStream())
                    {
                        if (isChunked)
                        {
                            while (true)
                            {
                                var chuchkHead = ClientStreamReader.ReadLine();
                                var chunkSize = int.Parse(chuchkHead, NumberStyles.HexNumber);

                                if (chunkSize != 0)
                                {
                                    var buffer = ClientStreamReader.ReadBytes(chunkSize);
                                    requestBodyStream.Write(buffer, 0, buffer.Length);
                                    //chunk trail
                                    ClientStreamReader.ReadLine();
                                }
                                else
                                {
                                    ClientStreamReader.ReadLine();
                                    break;
                                }
                            }
                        }
                        try
                        {
                            switch (requestContentEncoding)
                            {
                                case "gzip":
                                    RequestBody = CompressionHelper.DecompressGzip(requestBodyStream);
                                    break;
                                case "deflate":
                                    RequestBody = CompressionHelper.DecompressDeflate(requestBodyStream);
                                    break;
                                case "zlib":
                                    RequestBody = CompressionHelper.DecompressGzip(requestBodyStream);
                                    break;
                                default:
                                    RequestBody = requestBodyStream.ToArray();
                                    break;
                            }
                        }
                        catch
                        {
                            RequestBody = requestBodyStream.ToArray();
                        }
                    }
                }
            }
            RequestBodyRead = true;
        }

        private void ReadResponseBody()
        {
            if (ResponseBody == null)
            {
                switch (ServerResponse.ContentEncoding)
                {
                    case "gzip":
                        ResponseBody = CompressionHelper.DecompressGzip(ResponseStream);
                        break;
                    case "deflate":
                        ResponseBody = CompressionHelper.DecompressDeflate(ResponseStream);
                        break;
                    case "zlib":
                        ResponseBody = CompressionHelper.DecompressZlib(ResponseStream);
                        break;
                    default:
                        ResponseBody = DecodeData(ResponseStream);
                        break;
                }

                ResponseBodyRead = true;
            }
        }


        //stream reader not recomended for images
        private byte[] DecodeData(Stream responseStream)
        {
            var buffer = new byte[_bufferSize];
            using (var ms = new MemoryStream())
            {
                int read;
                while ((read = responseStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ms.Write(buffer, 0, read);
                }
                return ms.ToArray();
            }
        }

        public Encoding GetRequestBodyEncoding()
        {
            if (RequestLocked) throw new Exception("You cannot call this function after request is made to server.");

            return RequestEncoding;
        }

        public byte[] GetRequestBody()
        {
            if (RequestLocked) throw new Exception("You cannot call this function after request is made to server.");

            ReadRequestBody();
            return RequestBody;
        }

        public string GetRequestBodyAsString()
        {
            if (RequestLocked) throw new Exception("You cannot call this function after request is made to server.");


            ReadRequestBody();

            return RequestBodyString ?? (RequestBodyString = RequestEncoding.GetString(RequestBody));
        }

        public void SetRequestBody(byte[] body)
        {
            if (RequestLocked) throw new Exception("You cannot call this function after request is made to server.");

            if (!RequestBodyRead)
            {
                ReadRequestBody();
            }

            RequestBody = body;
            RequestBodyRead = true;
        }

        public void SetRequestBodyString(string body)
        {
            if (RequestLocked) throw new Exception("Youcannot call this function after request is made to server.");

            if (!RequestBodyRead)
            {
                ReadRequestBody();
            }

            RequestBody = RequestEncoding.GetBytes(body);
            RequestBodyRead = true;
        }

        public Encoding GetResponseBodyEncoding()
        {
            if (!RequestLocked) throw new Exception("You cannot call this function before request is made to server.");

            return ResponseEncoding;
        }

        public byte[] GetResponseBody()
        {
            if (!RequestLocked) throw new Exception("You cannot call this function before request is made to server.");

            ReadResponseBody();
            return ResponseBody;
        }

        public string GetResponseBodyAsString()
        {
            if (!RequestLocked) throw new Exception("You cannot call this function before request is made to server.");

            GetResponseBody();

            return ResponseBodyString ?? (ResponseBodyString = ResponseEncoding.GetString(ResponseBody));
        }

        public void SetResponseBody(byte[] body)
        {
            if (!RequestLocked) throw new Exception("You cannot call this function before request is made to server.");

            if (ResponseBody == null)
            {
                GetResponseBody();
            }

            ResponseBody = body;
        }

        public void SetResponseBodyString(string body)
        {
            if (!RequestLocked) throw new Exception("You cannot call this function before request is made to server.");

            if (ResponseBody == null)
            {
                GetResponseBody();
            }

            var bodyBytes = ResponseEncoding.GetBytes(body);
            SetResponseBody(bodyBytes);
        }


        public void Ok(string html)
        {
            if (RequestLocked) throw new Exception("You cannot call this function after request is made to server.");

            if (html == null)
                html = string.Empty;

            var result = Encoding.Default.GetBytes(html);

            var connectStreamWriter = new StreamWriter(ClientStream);
            var s = string.Format("HTTP/{0}.{1} {2} {3}", RequestHttpVersion.Major, RequestHttpVersion.Minor, 200, "Ok");
            connectStreamWriter.WriteLine(s);
            connectStreamWriter.WriteLine("Timestamp: {0}", DateTime.Now);
            connectStreamWriter.WriteLine("content-length: " + result.Length);
            connectStreamWriter.WriteLine("Cache-Control: no-cache, no-store, must-revalidate");
            connectStreamWriter.WriteLine("Pragma: no-cache");
            connectStreamWriter.WriteLine("Expires: 0");

            connectStreamWriter.WriteLine(RequestIsAlive ? "Connection: Keep-Alive" : "Connection: close");

            connectStreamWriter.WriteLine();
            connectStreamWriter.Flush();

            ClientStream.Write(result, 0, result.Length);


            CancelRequest = true;
        }
    }
}
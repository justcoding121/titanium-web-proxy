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
    public class Client
    {
        internal TcpClient TcpClient { get; set; }
        internal Stream ClientStream { get; set; }
        internal CustomBinaryReader ClientStreamReader { get; set; }
        internal StreamWriter ClientStreamWriter { get; set; }

        public int ClientPort { get; internal set; }
        public IPAddress ClientIpAddress { get; internal set; }

    }
    public class SessionEventArgs : EventArgs, IDisposable
    {
        readonly int _bufferSize;

        internal SessionEventArgs(int bufferSize)
        {
            _bufferSize = bufferSize;
            Client = new Client();
            ProxySession = new HttpWebSession();
        }

        public Client Client { get; set; }

        public bool IsHttps { get; internal set; }

        public HttpWebSession ProxySession { get; set; }



        public int RequestContentLength
        {
            get
            {
                if (ProxySession.Request.RequestHeaders.All(x => x.Name.ToLower() != "content-length")) return -1;
                int contentLen;
                int.TryParse(ProxySession.Request.RequestHeaders.First(x => x.Name.ToLower() == "content-length").Value, out contentLen);
                if (contentLen != 0)
                    return contentLen;
                return -1;
            }
        }

        public string RequestMethod
        {
            get { return ProxySession.Request.Method; }
        }


        public string ResponseStatusCode
        {
            get { return ProxySession.Response.ResponseStatusCode; }
        }

        public string ResponseContentType
        {
            get
            {
                return ProxySession.Response.ResponseHeaders.Any(x => x.Name.ToLower() == "content-type")
                    ? ProxySession.Response.ResponseHeaders.First(x => x.Name.ToLower() == "content-type").Value
                    : null;
            }
        }

        public void Dispose()
        {

        }

        private void ReadRequestBody()
        {
            if ((ProxySession.Request.Method.ToUpper() != "POST" && ProxySession.Request.Method.ToUpper() != "PUT"))
            {
                throw new BodyNotFoundException("Request don't have a body." +
                                                "Please verify that this request is a Http POST/PUT and request content length is greater than zero before accessing the body.");
            }

            if (ProxySession.Request.RequestBody == null)
            {
                var isChunked = false;
                string requestContentEncoding = null;


                if (ProxySession.Request.RequestHeaders.Any(x => x.Name.ToLower() == "content-encoding"))
                {
                    requestContentEncoding = ProxySession.Request.RequestHeaders.First(x => x.Name.ToLower() == "content-encoding").Value;
                }

                if (ProxySession.Request.RequestHeaders.Any(x => x.Name.ToLower() == "transfer-encoding"))
                {
                    var transferEncoding =
                        ProxySession.Request.RequestHeaders.First(x => x.Name.ToLower() == "transfer-encoding").Value.ToLower();
                    if (transferEncoding.Contains("chunked"))
                    {
                        isChunked = true;
                    }
                }


                if (requestContentEncoding == null && !isChunked)
                    ProxySession.Request.RequestBody = this.Client.ClientStreamReader.ReadBytes(RequestContentLength);
                else
                {
                    using (var requestBodyStream = new MemoryStream())
                    {
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
                                    this.Client.ClientStreamReader.ReadLine();
                                    break;
                                }
                            }
                        }

                        try
                        {
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
                            ProxySession.Request.RequestBody = requestBodyStream.ToArray();
                        }
                    }
                }
            }
            ProxySession.Request.RequestBodyRead = true;
        }

        private void ReadResponseBody()
        {
            if (ProxySession.Response.ResponseBody == null)
            {
                using (var responseBodyStream = new MemoryStream())
                {
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
                                ProxySession.ProxyClient.ServerStreamReader.ReadLine();
                                break;
                            }
                        }
                    }
                    else
                    {
                        var buffer = ProxySession.ProxyClient.ServerStreamReader.ReadBytes(ProxySession.Response.ContentLength);
                        responseBodyStream.Write(buffer, 0, buffer.Length);
                    }

                    switch (ProxySession.Response.ResponseContentEncoding)
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

                ProxySession.Response.ResponseBodyRead = true;
            }
        }



        public Encoding GetRequestBodyEncoding()
        {
            if (ProxySession.Request.RequestLocked) throw new Exception("You cannot call this function after request is made to server.");

            return ProxySession.Request.RequestEncoding;
        }

        public byte[] GetRequestBody()
        {
            if (ProxySession.Request.RequestLocked) throw new Exception("You cannot call this function after request is made to server.");

            ReadRequestBody();
            return ProxySession.Request.RequestBody;
        }

        public string GetRequestBodyAsString()
        {
            if (ProxySession.Request.RequestLocked) throw new Exception("You cannot call this function after request is made to server.");


            ReadRequestBody();

            return ProxySession.Request.RequestBodyString ?? (ProxySession.Request.RequestBodyString = ProxySession.Request.RequestEncoding.GetString(ProxySession.Request.RequestBody));
        }

        public void SetRequestBody(byte[] body)
        {
            if (ProxySession.Request.RequestLocked) throw new Exception("You cannot call this function after request is made to server.");

            if (!ProxySession.Request.RequestBodyRead)
            {
                ReadRequestBody();
            }

            ProxySession.Request.RequestBody = body;
            ProxySession.Request.RequestBodyRead = true;
        }

        public void SetRequestBodyString(string body)
        {
            if (ProxySession.Request.RequestLocked) throw new Exception("You cannot call this function after request is made to server.");

            if (!ProxySession.Request.RequestBodyRead)
            {
                ReadRequestBody();
            }

            ProxySession.Request.RequestBody = ProxySession.Request.RequestEncoding.GetBytes(body);
            ProxySession.Request.RequestBodyRead = true;
        }

        public Encoding GetResponseBodyEncoding()
        {
            if (!ProxySession.Request.RequestLocked) throw new Exception("You cannot call this function before request is made to server.");

            return ProxySession.Response.ResponseEncoding;
        }

        public byte[] GetResponseBody()
        {
            if (!ProxySession.Request.RequestLocked) throw new Exception("You cannot call this function before request is made to server.");

            ReadResponseBody();
            return ProxySession.Response.ResponseBody;
        }

        public string GetResponseBodyAsString()
        {
            if (!ProxySession.Request.RequestLocked) throw new Exception("You cannot call this function before request is made to server.");

            GetResponseBody();

            return ProxySession.Response.ResponseBodyString ?? (ProxySession.Response.ResponseBodyString = ProxySession.Response.ResponseEncoding.GetString(ProxySession.Response.ResponseBody));
        }

        public void SetResponseBody(byte[] body)
        {
            if (!ProxySession.Request.RequestLocked) throw new Exception("You cannot call this function before request is made to server.");

            if (ProxySession.Response.ResponseBody == null)
            {
                GetResponseBody();
            }

            ProxySession.Response.ResponseBody = body;
        }

        public void SetResponseBodyString(string body)
        {
            if (!ProxySession.Request.RequestLocked) throw new Exception("You cannot call this function before request is made to server.");

            if (ProxySession.Response.ResponseBody == null)
            {
                GetResponseBody();
            }

            var bodyBytes = ProxySession.Response.ResponseEncoding.GetBytes(body);
            SetResponseBody(bodyBytes);
        }


        public void Ok(string html)
        {
            if (ProxySession.Request.RequestLocked) throw new Exception("You cannot call this function after request is made to server.");

            if (html == null)
                html = string.Empty;

            var result = Encoding.Default.GetBytes(html);

            var connectStreamWriter = new StreamWriter(this.Client.ClientStream);
            var s = string.Format("HTTP/{0}.{1} {2} {3}", ProxySession.Request.RequestHttpVersion.Major, ProxySession.Request.RequestHttpVersion.Minor, 200, "Ok");
            connectStreamWriter.WriteLine(s);
            connectStreamWriter.WriteLine("Timestamp: {0}", DateTime.Now);
            connectStreamWriter.WriteLine("content-length: " + result.Length);
            connectStreamWriter.WriteLine("Cache-Control: no-cache, no-store, must-revalidate");
            connectStreamWriter.WriteLine("Pragma: no-cache");
            connectStreamWriter.WriteLine("Expires: 0");

            connectStreamWriter.WriteLine(ProxySession.Request.RequestIsAlive ? "Connection: Keep-Alive" : "Connection: close");

            connectStreamWriter.WriteLine();
            connectStreamWriter.Flush();

            this.Client.ClientStream.Write(result, 0, result.Length);


            ProxySession.Request.CancelRequest = true;
        }
    }
}
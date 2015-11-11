using EndPointProxy.Extensions;
using ProxyLanguage;
using ProxyLanguage.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace EndPointProxy
{
    public class EndPointProxyRequest : IProxyRequest
    {
        private static readonly char[] SemiSplit = { ';' };

        HttpWebRequest _proxyRequest;

        public EndPointProxyRequest(Uri requestUri, string httpMethod, Version version)
        {
            RequestUri = requestUri;

            _proxyRequest = (HttpWebRequest)WebRequest.Create(requestUri);
            _proxyRequest.Proxy = null;
            _proxyRequest.UseDefaultCredentials = true;
            _proxyRequest.Method = httpMethod;
            _proxyRequest.ProtocolVersion = version;

            _proxyRequest.AllowAutoRedirect = false;
            _proxyRequest.AutomaticDecompression = DecompressionMethods.None;
            _proxyRequest.AllowWriteStreamBuffering = true;

            _proxyRequest.ConnectionGroupName = RequestUri.Host;            
        }

        public Uri RequestUri { get; private set; }
        public bool KeepAlive {get { return _proxyRequest.KeepAlive; } }
        public Encoding RequestEncoding { get { return _proxyRequest.GetEncoding(); } }

        public string Method { get { return _proxyRequest.Method; } }

        public long ContentLength { get { return _proxyRequest.ContentLength; } set { _proxyRequest.ContentLength = value; } }

        public bool AllowWriteStreamBuffering { get { return _proxyRequest.AllowWriteStreamBuffering; } set { _proxyRequest.AllowWriteStreamBuffering = value; } }
        public bool SendChunked { get { return _proxyRequest.SendChunked; } }

        public void SetRequestHeaders(List<HttpHeader> requestHeaders)
        {
            var webRequest = _proxyRequest;

            for (var i = 0; i < requestHeaders.Count; i++)
            {
                switch (requestHeaders[i].Name.ToLower())
                {
                    case "accept":
                        webRequest.Accept = requestHeaders[i].Value;
                        break;
                    case "accept-encoding":
                        webRequest.Headers.Add("Accept-Encoding", "gzip,deflate,zlib");
                        break;
                    case "cookie":
                        webRequest.Headers["Cookie"] = requestHeaders[i].Value;
                        break;
                    case "connection":
                        if (requestHeaders[i].Value.ToLower() == "keep-alive")
                            webRequest.KeepAlive = true;

                        break;
                    case "content-length":
                        int contentLen;
                        int.TryParse(requestHeaders[i].Value, out contentLen);
                        if (contentLen != 0)
                            webRequest.ContentLength = contentLen;
                        break;
                    case "content-type":
                        webRequest.ContentType = requestHeaders[i].Value;
                        break;
                    case "expect":
                        if (requestHeaders[i].Value.ToLower() == "100-continue")
                            webRequest.ServicePoint.Expect100Continue = true;
                        else
                            webRequest.Expect = requestHeaders[i].Value;
                        break;
                    case "host":
                        webRequest.Host = requestHeaders[i].Value;
                        break;
                    case "if-modified-since":
                        var sb = requestHeaders[i].Value.Trim().Split(SemiSplit);
                        DateTime d;
                        if (DateTime.TryParse(sb[0], out d))
                            webRequest.IfModifiedSince = d;
                        break;
                    case "proxy-connection":
                        if (requestHeaders[i].Value.ToLower() == "keep-alive")
                            webRequest.KeepAlive = true;
                        break;
                    case "range":
                        var startEnd = requestHeaders[i].Value.Replace(Environment.NewLine, "").Remove(0, 6).Split('-');
                        if (startEnd.Length > 1)
                        {
                            if (!string.IsNullOrEmpty(startEnd[1]))
                                webRequest.AddRange(int.Parse(startEnd[0]), int.Parse(startEnd[1]));
                            else webRequest.AddRange(int.Parse(startEnd[0]));
                        }
                        else
                            webRequest.AddRange(int.Parse(startEnd[0]));
                        break;
                    case "referer":
                        webRequest.Referer = requestHeaders[i].Value;
                        break;
                    case "user-agent":
                        webRequest.UserAgent = requestHeaders[i].Value;
                        break;

                    //revisit this, transfer-encoding is not a request header according to spec
                    //But how to identify if client is sending chunked body for PUT/POST?
                    case "transfer-encoding":
                        if (requestHeaders[i].Value.ToLower().Contains("chunked"))
                            webRequest.SendChunked = true;
                        else
                            webRequest.SendChunked = false;
                        break;
                    case "upgrade":
                        if (requestHeaders[i].Value.ToLower() == "http/1.1")
                            webRequest.Headers.Add("Upgrade", requestHeaders[i].Value);
                        break;

                    default:
                        webRequest.Headers.Add(requestHeaders[i].Name, requestHeaders[i].Value);

                        break;
                }
            }
        }

        public void Abort()
        {
            _proxyRequest.Abort();
        }

        public Stream GetRequestStream()
        {
            return new EndpointProxyStream(_proxyRequest.GetRequestStream());
        }

        public IAsyncResult BeginGetResponse(AsyncCallback asyncResult, object args)
        {
            return _proxyRequest.BeginGetResponse(asyncResult, args);
        }

        public WebResponse EndGetResponse(IAsyncResult asyncResult)
        {
            return _proxyRequest.EndGetResponse(asyncResult);
        }   
    }
}

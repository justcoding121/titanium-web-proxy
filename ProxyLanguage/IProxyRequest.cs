using ProxyLanguage.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace ProxyLanguage
{
    public interface IProxyRequest
    {
        Uri RequestUri { get; }
        bool KeepAlive { get; }
        Encoding RequestEncoding { get; }

        long ContentLength { get; set; }

        string Method { get; }

        bool AllowWriteStreamBuffering { get; set; }
        bool SendChunked { get; }

        void SetRequestHeaders(List<HttpHeader> requestHeaders);
        void Abort();

        Stream GetRequestStream();
        IAsyncResult BeginGetResponse(AsyncCallback asyncResult, object args);
        WebResponse EndGetResponse(IAsyncResult asyncResult);
    }
}

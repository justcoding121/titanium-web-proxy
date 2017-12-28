using System;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.EventArguments
{
    public class MultipartRequestPartSentEventArgs : EventArgs
    {
        public string Boundary { get; }

        public HeaderCollection Headers { get; }

        public MultipartRequestPartSentEventArgs(string boundary, HeaderCollection headers)
        {
            Boundary = boundary;
            Headers = headers;
        }
    }
}
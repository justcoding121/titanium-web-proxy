using System;
using System.Buffers;
using System.IO;
using System.Text;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy.Http
{
    internal class HeaderBuilder
    {
        private MemoryStream stream = new MemoryStream();

        public void WriteRequestLine(string httpMethod, string httpUrl, Version version)
        {
            // "{httpMethod} {httpUrl} HTTP/{version.Major}.{version.Minor}";

            Write(httpMethod);
            Write(" ");
            Write(httpUrl);
            Write(" HTTP/");
            Write(version.Major.ToString());
            Write(".");
            Write(version.Minor.ToString());
            WriteLine();
        }

        public void WriteResponseLine(Version version, int statusCode, string statusDescription)
        {
            // "HTTP/{version.Major}.{version.Minor} {statusCode} {statusDescription}";

            Write("HTTP/");
            Write(version.Major.ToString());
            Write(".");
            Write(version.Minor.ToString());
            Write(" ");
            Write(statusCode.ToString());
            Write(" ");
            Write(statusDescription);
            WriteLine();
        }

        public void WriteHeaders(HeaderCollection headers)
        {
            foreach (var header in headers)
            {
                WriteHeader(header);
            }

            WriteLine();
        }

        public void WriteHeader(HttpHeader header)
        {
            Write(header.Name);
            Write(": ");
            Write(header.Value);
            WriteLine();
        }

        public void WriteLine()
        {
            var data = ProxyConstants.NewLineBytes;
            stream.Write(data, 0, data.Length);
        }

        public void Write(string str)
        {
            var encoding = HttpHelper.HeaderEncoding;

#if NETSTANDARD2_1
            var buf = ArrayPool<byte>.Shared.Rent(str.Length * 4);
            var span = new Span<byte>(buf);

            int bytes = encoding.GetBytes(str.AsSpan(), span);

            stream.Write(span.Slice(0, bytes));
            ArrayPool<byte>.Shared.Return(buf);
#else
            var data = encoding.GetBytes(str);
            stream.Write(data, 0, data.Length);
#endif
        }

        public byte[] GetBytes()
        {
            return stream.ToArray();
        }
    }
}

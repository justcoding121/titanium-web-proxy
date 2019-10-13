using System.IO;
using System.Text;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy.IntegrationTests.Helpers
{
    internal static class HttpMessageParsing
    {
        private static readonly char[] colonSplit = { ':' };

        /// <summary>
        /// This is a terribly inefficient way of reading & parsing an
        /// http request, but it's good enough for testing purposes.
        /// </summary>
        /// <param name="messageText">The request message</param>
        /// <param name="requireBody"></param>
        /// <returns>Request object if message complete, null otherwise</returns>
        internal static Request ParseRequest(string messageText, bool requireBody)
        {
            var reader = new StringReader(messageText);
            var line = reader.ReadLine();
            if (string.IsNullOrEmpty(line))
                return null;

            try
            {
                Request.ParseRequestLine(line, out var method, out var url, out var version);
                RequestResponseBase request = new Request
                {
                    Method = method, RequestUriString = url, HttpVersion = version
                };
                while (!string.IsNullOrEmpty(line = reader.ReadLine()))
                {
                    var header = line.Split(colonSplit, 2);
                    request.Headers.AddHeader(header[0], header[1]);
                }

                // First zero-length line denotes end of headers. If we
                // didn't get one, then we're not done with request
                if (line?.Length != 0)
                    return null;

                if (!requireBody)
                    return request as Request;

                if (parseBody(reader, ref request))
                    return request as Request;
            }
            catch
            {
                // ignore
            }

            return null;
        }

        /// <summary>
        /// This is a terribly inefficient way of reading & parsing an
        /// http response, but it's good enough for testing purposes.
        /// </summary>
        /// <param name="messageText">The response message</param>
        /// <returns>Response object if message complete, null otherwise</returns>
        internal static Response ParseResponse(string messageText)
        {
            var reader = new StringReader(messageText);
            var line = reader.ReadLine();
            if (string.IsNullOrEmpty(line))
                return null;

            try
            {
                Response.ParseResponseLine(line, out var version, out var status, out var desc);
                RequestResponseBase response = new Response
                {
                    HttpVersion = version, StatusCode = status, StatusDescription = desc
                };

                while (!string.IsNullOrEmpty(line = reader.ReadLine()))
                {
                    var header = line.Split(colonSplit, 2);
                    response.Headers.AddHeader(header[0], header[1]);
                }

                // First zero-length line denotes end of headers. If we
                // didn't get one, then we're not done with response
                if (line?.Length != 0)
                    return null;

                if (parseBody(reader, ref response))
                    return response as Response;
            }
            catch
            {
                // ignore
            }

            return null;
        }

        private static bool parseBody(StringReader reader, ref RequestResponseBase obj)
        {
            obj.OriginalContentLength = obj.ContentLength;
            if (obj.ContentLength <= 0)
            {
                // no body, done
                return true;
            }

            obj.Body = Encoding.ASCII.GetBytes(reader.ReadToEnd());
                
            // done reading body
            return obj.ContentLength == obj.OriginalContentLength;
        }
    }
}

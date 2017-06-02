using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Http
{
    /// <summary>
    /// A HTTP(S) request object
    /// </summary>
    public class Request : IDisposable
    {
        /// <summary>
        /// Request Method
        /// </summary>
        public string Method { get; set; }

        /// <summary>
        /// Request HTTP Uri
        /// </summary>
        public Uri RequestUri { get; set; }

        /// <summary>
        /// Request Http Version
        /// </summary>
        public Version HttpVersion { get; set; }

        /// <summary>
        /// Has request body?
        /// </summary>
        public bool HasBody => Method == "POST" || Method == "PUT" || Method == "PATCH";

        /// <summary>
        /// Request Http hostanem
        /// </summary>
        internal string Host
        {
            get
            {
                var hasHeader = RequestHeaders.ContainsKey("host");
                return hasHeader ? RequestHeaders["host"].Value : null;
            }
            set
            {
                var hasHeader = RequestHeaders.ContainsKey("host");
                if (hasHeader)
                {
                    RequestHeaders["host"].Value = value;
                }
                else
                {
                    RequestHeaders.Add("Host", new HttpHeader("Host", value));
                }
            }
        }

        /// <summary>
        /// Request content encoding
        /// </summary>
        internal string ContentEncoding
        {
            get
            {
                var hasHeader = RequestHeaders.ContainsKey("content-encoding");

                if (hasHeader)
                {
                    return RequestHeaders["content-encoding"].Value;
                }

                return null;
            }
        }

        /// <summary>
        /// Request content-length
        /// </summary>
        public long ContentLength
        {
            get
            {
                var hasHeader = RequestHeaders.ContainsKey("content-length");

                if (hasHeader == false)
                {
                    return -1;
                }

                var header = RequestHeaders["content-length"];

                long contentLen;
                long.TryParse(header.Value, out contentLen);
                if (contentLen >= 0)
                {
                    return contentLen;
                }

                return -1;
            }
            set
            {
                var hasHeader = RequestHeaders.ContainsKey("content-length");

                var header = RequestHeaders["content-length"];

                if (value >= 0)
                {
                    if (hasHeader)
                    {
                        header.Value = value.ToString();
                    }
                    else
                    {
                        RequestHeaders.Add("content-length", new HttpHeader("content-length", value.ToString()));
                    }

                    IsChunked = false;
                }
                else
                {
                    if (hasHeader)
                    {
                        RequestHeaders.Remove("content-length");
                    }
                }
            }
        }

        /// <summary>
        /// Request content-type
        /// </summary>
        public string ContentType
        {
            get
            {
                var hasHeader = RequestHeaders.ContainsKey("content-type");

                if (hasHeader)
                {
                    var header = RequestHeaders["content-type"];
                    return header.Value;
                }

                return null;
            }
            set
            {
                var hasHeader = RequestHeaders.ContainsKey("content-type");

                if (hasHeader)
                {
                    var header = RequestHeaders["content-type"];
                    header.Value = value;
                }
                else
                {
                    RequestHeaders.Add("content-type", new HttpHeader("content-type", value));
                }
            }
        }

        /// <summary>
        /// Is request body send as chunked bytes
        /// </summary>
        public bool IsChunked
        {
            get
            {
                var hasHeader = RequestHeaders.ContainsKey("transfer-encoding");

                if (hasHeader)
                {
                    var header = RequestHeaders["transfer-encoding"];

                    return header.Value.ContainsIgnoreCase("chunked");
                }

                return false;
            }
            set
            {
                var hasHeader = RequestHeaders.ContainsKey("transfer-encoding");

                if (value)
                {
                    if (hasHeader)
                    {
                        var header = RequestHeaders["transfer-encoding"];
                        header.Value = "chunked";
                    }
                    else
                    {
                        RequestHeaders.Add("transfer-encoding", new HttpHeader("transfer-encoding", "chunked"));
                    }

                    ContentLength = -1;
                }
                else
                {
                    if (hasHeader)
                    {
                        RequestHeaders.Remove("transfer-encoding");
                    }
                }
            }
        }

        /// <summary>
        /// Does this request has a 100-continue header?
        /// </summary>
        public bool ExpectContinue
        {
            get
            {
                var hasHeader = RequestHeaders.ContainsKey("expect");

                if (!hasHeader) return false;
                var header = RequestHeaders["expect"];

                return header.Value.Equals("100-continue");
            }
        }

        /// <summary>
        /// Request Url
        /// </summary>
        public string Url => RequestUri.OriginalString;

        /// <summary>
        /// Encoding for this request
        /// </summary>
        internal Encoding Encoding => this.GetEncoding();

        /// <summary>
        /// Terminates the underlying Tcp Connection to client after current request
        /// </summary>
        internal bool CancelRequest { get; set; }

        /// <summary>
        /// Request body as byte array
        /// </summary>
        internal byte[] RequestBody { get; set; }

        /// <summary>
        /// request body as string
        /// </summary>
        internal string RequestBodyString { get; set; }

        /// <summary>
        /// Request body was read by user?
        /// </summary>
        internal bool RequestBodyRead { get; set; }

        /// <summary>
        /// Request is ready to be sent (user callbacks are complete?)
        /// </summary>
        internal bool RequestLocked { get; set; }

        /// <summary>
        /// Does this request has an upgrade to websocket header?
        /// </summary>
        internal bool UpgradeToWebSocket
        {
            get
            {
                var hasHeader = RequestHeaders.ContainsKey("upgrade");

                if (hasHeader == false)
                {
                    return false;
                }

                var header = RequestHeaders["upgrade"];

                return header.Value.Equals("websocket", StringComparison.CurrentCultureIgnoreCase);
            }
        }

        /// <summary>
        /// Unique Request header collection
        /// </summary>
        public Dictionary<string, HttpHeader> RequestHeaders { get; set; }

        /// <summary>
        /// Non Unique headers
        /// </summary>
        public Dictionary<string, List<HttpHeader>> NonUniqueRequestHeaders { get; set; }

        /// <summary>
        /// Does server responsed positively for 100 continue request
        /// </summary>
        public bool Is100Continue { get; internal set; }

        /// <summary>
        /// Server responsed negatively for the request for 100 continue
        /// </summary>
        public bool ExpectationFailed { get; internal set; }

        /// <summary>
        /// Constructor.
        /// </summary>
        public Request()
        {
            RequestHeaders = new Dictionary<string, HttpHeader>(StringComparer.OrdinalIgnoreCase);
            NonUniqueRequestHeaders = new Dictionary<string, List<HttpHeader>>(StringComparer.OrdinalIgnoreCase);
        }


        /// <summary>
        /// True if header exists
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public bool HeaderExists(string name)
        {
            if (RequestHeaders.ContainsKey(name)
                || NonUniqueRequestHeaders.ContainsKey(name))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Returns all headers with given name if exists
        /// Returns null if does'nt exist
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public List<HttpHeader> GetHeaders(string name)
        {
            if (RequestHeaders.ContainsKey(name))
            {
                return new List<HttpHeader>() { RequestHeaders[name] };
            }
            else if (NonUniqueRequestHeaders.ContainsKey(name))
            {
                return new List<HttpHeader>(NonUniqueRequestHeaders[name]);
            }

            return null;
        }

        /// <summary>
        /// Returns all headers 
        /// </summary>
        /// <returns></returns>
        public List<HttpHeader> GetAllHeaders()
        {
            var result = new List<HttpHeader>();

            result.AddRange(RequestHeaders.Select(x => x.Value));
            result.AddRange(NonUniqueRequestHeaders.SelectMany(x => x.Value));

            return result;
        }

        /// <summary>
        /// Add a new header with given name and value
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void AddHeader(string name, string value)
        {
            AddHeader(new HttpHeader(name, value));
        }

        /// <summary>
        /// Adds the given header object to Request
        /// </summary>
        /// <param name="newHeader"></param>
        public void AddHeader(HttpHeader newHeader)
        {
            if (NonUniqueRequestHeaders.ContainsKey(newHeader.Name))
            {
                NonUniqueRequestHeaders[newHeader.Name].Add(newHeader);
                return;
            }

            if (RequestHeaders.ContainsKey(newHeader.Name))
            {
                var existing = RequestHeaders[newHeader.Name];
                RequestHeaders.Remove(newHeader.Name);

                NonUniqueRequestHeaders.Add(newHeader.Name,
                    new List<HttpHeader>() { existing, newHeader });
            }
            else
            {
                RequestHeaders.Add(newHeader.Name, newHeader);
            }
        }

        /// <summary>
        ///  removes all headers with given name
        /// </summary>
        /// <param name="headerName"></param>
        /// <returns>True if header was removed
        /// False if no header exists with given name</returns>
        public bool RemoveHeader(string headerName)
        {
            if (RequestHeaders.ContainsKey(headerName))
            {
                RequestHeaders.Remove(headerName);
                return true;
            }
            else if (NonUniqueRequestHeaders.ContainsKey(headerName))
            {
                NonUniqueRequestHeaders.Remove(headerName);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Removes given header object if it exist
        /// </summary>
        /// <param name="header">Returns true if header exists and was removed </param>
        public bool RemoveHeader(HttpHeader header)
        {
            if (RequestHeaders.ContainsKey(header.Name))
            {
                if (RequestHeaders[header.Name].Equals(header))
                {
                    RequestHeaders.Remove(header.Name);
                    return true;
                }

            }
            else if (NonUniqueRequestHeaders.ContainsKey(header.Name))
            {
                if (NonUniqueRequestHeaders[header.Name]
                    .RemoveAll(x => x.Equals(header)) > 0)
                {
                    return true;
                }
            }

            return false;
        }


        /// <summary>
        /// Dispose off 
        /// </summary>
        public void Dispose()
        {
            //not really needed since GC will collect it
            //but just to be on safe side

            RequestHeaders = null;
            NonUniqueRequestHeaders = null;

            RequestBody = null;
            RequestBody = null;
        }

      
    }
}

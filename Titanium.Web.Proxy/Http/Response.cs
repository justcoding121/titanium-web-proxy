using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Extensions;
using System;

namespace Titanium.Web.Proxy.Http
{
    /// <summary>
    /// Http(s) response object
    /// </summary>
    public class Response
    {
        public string ResponseStatusCode { get; set; }
        public string ResponseStatusDescription { get; set; }

        internal Encoding Encoding { get { return this.GetResponseCharacterEncoding(); } }

        
        internal string ContentEncoding
        {
            get
            {
                var header = this.ResponseHeaders.FirstOrDefault(x => x.Name.ToLower().Equals("content-encoding"));

                if (header != null)
                {
                    return header.Value.Trim();
                }

                return null;
            }
        }

        internal Version HttpVersion { get; set; }
        internal bool ResponseKeepAlive
        {
            get
            {
                var header = this.ResponseHeaders.FirstOrDefault(x => x.Name.ToLower().Equals("connection"));

                if (header != null && header.Value.ToLower().Contains("close"))
                {
                    return false;
                }

                return true;

            }
        }

        public string ContentType
        {
            get
            {
                var header = this.ResponseHeaders.FirstOrDefault(x => x.Name.ToLower().Equals("content-type"));

                if (header != null)
                {
                    return header.Value;
                }

                return null;

            }
        }

        internal long ContentLength
        {
            get
            {
                var header = this.ResponseHeaders.FirstOrDefault(x => x.Name.ToLower().Equals("content-length"));

                if (header == null)
                    return -1;

                long contentLen;
                long.TryParse(header.Value, out contentLen);
                if (contentLen >= 0)
                    return contentLen;

                return -1;

            }
            set
            {
                var header = this.ResponseHeaders.FirstOrDefault(x => x.Name.ToLower().Equals("content-length"));

                if (value >= 0)
                {
                    if (header != null)
                        header.Value = value.ToString();
                    else
                        ResponseHeaders.Add(new HttpHeader("content-length", value.ToString()));

                    IsChunked = false;
                }
                else
                {
                    if (header != null)
                        this.ResponseHeaders.Remove(header);
                }
            }
        }

        internal bool IsChunked
        {
            get
            {
                var header = this.ResponseHeaders.FirstOrDefault(x => x.Name.ToLower().Equals("transfer-encoding"));

                if (header != null && header.Value.ToLower().Contains("chunked"))
                {
                    return true;
                }

                return false;

            }
            set
            {
                var header = this.ResponseHeaders.FirstOrDefault(x => x.Name.ToLower().Equals("transfer-encoding"));

                if (value)
                {
                    if (header != null)
                    {
                        header.Value = "chunked";
                    }
                    else
                        ResponseHeaders.Add(new HttpHeader("transfer-encoding", "chunked"));

                    this.ContentLength = -1;
                }
                else
                {
                    if (header != null)
                        ResponseHeaders.Remove(header);
                }

            }
        }

        public List<HttpHeader> ResponseHeaders { get; set; }

        internal Stream ResponseStream { get; set; }
        internal byte[] ResponseBody { get; set; }
        internal string ResponseBodyString { get; set; }
        internal bool ResponseBodyRead { get; set; }
        internal bool ResponseLocked { get; set; }
        public bool Is100Continue { get; internal set; }
        public bool ExpectationFailed { get; internal set; }

        public Response()
        {
            this.ResponseHeaders = new List<HttpHeader>();
        }
    }

}

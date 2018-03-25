﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Http
{
    public abstract class RequestResponseBase
    {
        /// <summary>
        /// Cached body content as byte array
        /// </summary>
        private byte[] body;

        /// <summary>
        /// Cached body as string
        /// </summary>
        private string bodyString;

        /// <summary>
        /// Keeps the body data after the session is finished
        /// </summary>
        public bool KeepBody { get; set; }

        /// <summary>
        /// Http Version
        /// </summary>
        public Version HttpVersion { get; set; } = HttpHeader.VersionUnknown;

        /// <summary>
        /// Collection of all headers
        /// </summary>
        public HeaderCollection Headers { get; } = new HeaderCollection();

        /// <summary>
        /// Length of the body
        /// </summary>
        public long ContentLength
        {
            get
            {
                string headerValue = Headers.GetHeaderValueOrNull(KnownHeaders.ContentLength);

                if (headerValue == null)
                {
                    return -1;
                }

                if (long.TryParse(headerValue, out long contentLen) && contentLen >= 0)
                {
                    return contentLen;
                }

                return -1;
            }
            set
            {
                if (value >= 0)
                {
                    Headers.SetOrAddHeaderValue(KnownHeaders.ContentLength, value.ToString());
                    IsChunked = false;
                }
                else
                {
                    Headers.RemoveHeader(KnownHeaders.ContentLength);
                }
            }
        }

        /// <summary>
        /// Content encoding for this request/response
        /// </summary>
        public string ContentEncoding => Headers.GetHeaderValueOrNull(KnownHeaders.ContentEncoding)?.Trim();

        /// <summary>
        /// Encoding for this request/response
        /// </summary>
        public Encoding Encoding => HttpHelper.GetEncodingFromContentType(ContentType);

        /// <summary>
        /// Content-type of the request/response
        /// </summary>
        public string ContentType
        {
            get => Headers.GetHeaderValueOrNull(KnownHeaders.ContentType);
            set => Headers.SetOrAddHeaderValue(KnownHeaders.ContentType, value);
        }

        /// <summary>
        /// Is body send as chunked bytes
        /// </summary>
        public bool IsChunked
        {
            get
            {
                string headerValue = Headers.GetHeaderValueOrNull(KnownHeaders.TransferEncoding);
                return headerValue != null && headerValue.ContainsIgnoreCase(KnownHeaders.TransferEncodingChunked);
            }
            set
            {
                if (value)
                {
                    Headers.SetOrAddHeaderValue(KnownHeaders.TransferEncoding, KnownHeaders.TransferEncodingChunked);
                    ContentLength = -1;
                }
                else
                {
                    Headers.RemoveHeader(KnownHeaders.TransferEncoding);
                }
            }
        }

        public abstract string HeaderText { get; }

        /// <summary>
        /// Body as byte array
        /// </summary>
        [Browsable(false)]
        public byte[] Body
        {
            get
            {
                EnsureBodyAvailable();
                return body;
            }
            internal set
            {
                body = value;
                bodyString = null;
            }
        }
        internal abstract void EnsureBodyAvailable(bool throwWhenNotReadYet = true);

        /// <summary>
        /// Body as string
        /// Use the encoding specified to decode the byte[] data to string
        /// </summary>
        [Browsable(false)]
        public string BodyString => bodyString ?? (bodyString = Encoding.GetString(Body));

        /// <summary>
        /// Was the body read by user?
        /// </summary>
        public bool IsBodyRead { get; internal set; }

        /// <summary>
        /// Is the request/response no more modifyable by user (user callbacks complete?)
        /// </summary>
        internal bool Locked { get; set; }

        internal void UpdateContentLength()
        {
            ContentLength = IsChunked ? -1 : body.Length;
        }

        /// <summary>
        /// Finish the session
        /// </summary>
        internal void FinishSession()
        {
            if (!KeepBody)
            {
                body = null;
                bodyString = null;
            }
        }

        public override string ToString()
        {
            return HeaderText;
        }
    }
}

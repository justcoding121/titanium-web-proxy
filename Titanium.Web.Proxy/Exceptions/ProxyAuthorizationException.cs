using System;
using System.Collections.Generic;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Exceptions
{
    /// <summary>
    /// Proxy authorization exception
    /// </summary>
    public class ProxyAuthorizationException : ProxyException
    {
        /// <summary>
        /// Instantiate new instance
        /// </summary>
        /// <param name="message">Exception message</param>
        /// <param name="innerException">Inner exception associated to upstream proxy authorization</param>
        /// <param name="headers">Http's headers associated</param>
        public ProxyAuthorizationException(string message, Exception innerException, IEnumerable<HttpHeader> headers) : base(message, innerException)
        {
            Headers = headers;
        }

        /// <summary>
        /// Headers associated with the authorization exception
        /// </summary>
        public IEnumerable<HttpHeader> Headers { get; }
    }
}
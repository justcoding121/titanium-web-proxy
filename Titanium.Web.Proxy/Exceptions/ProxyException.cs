using System;

namespace Titanium.Web.Proxy.Exceptions
{
    /// <summary>
    /// Base class exception associated with this proxy implementation
    /// </summary>
    public abstract class ProxyException : Exception
    {
        /// <summary>
        /// Instantiate a new instance of this exception - must be invoked by derived classes' constructors
        /// </summary>
        /// <param name="message">Exception message</param>
        protected ProxyException(string message) : base(message)
        {
        }

        /// <summary>
        /// Instantiate this exception - must be invoked by derived classes' constructors
        /// </summary>
        /// <param name="message">Excception message</param>
        /// <param name="innerException">Inner exception associated</param>
        protected ProxyException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
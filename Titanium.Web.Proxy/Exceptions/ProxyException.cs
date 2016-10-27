using System;

namespace Titanium.Web.Proxy.Exceptions
{
    /// <summary>
    /// Base class exception associated with this proxy implementation
    /// </summary>
    public abstract class ProxyException : Exception
    {
        /// <summary>
        /// Instantiate this exception - must be invoked by derived classes' constructors
        /// </summary>
        /// <param name="message"></param>
        /// <param name="innerException"></param>
        protected ProxyException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
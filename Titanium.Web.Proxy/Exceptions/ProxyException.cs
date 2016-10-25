using System;

namespace Titanium.Web.Proxy.Exceptions
{
    /// <summary>
    /// Base class exception associated with this proxy implementation
    /// </summary>
    public abstract class ProxyException : Exception
    {
        protected ProxyException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
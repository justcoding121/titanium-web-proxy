using System;

namespace Titanium.Web.Proxy.Exceptions
{
    /// <summary>
    /// The server connection was closed upon first write with the new connection from pool.
    /// Should retry the request with a new connection.
    /// </summary>
    public class RetryableServerConnectionException : ProxyException
    {
        internal RetryableServerConnectionException(string message) : base(message)
        {
        }

        /// <summary>
        ///     Constructor.
        /// </summary>
        /// <param name="message"></param>
        /// <param name="e"></param>
        internal RetryableServerConnectionException(string message, Exception e) : base(message, e)
        {
        }
    }
}

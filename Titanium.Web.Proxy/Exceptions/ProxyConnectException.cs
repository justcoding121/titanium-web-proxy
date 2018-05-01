using System;
using Titanium.Web.Proxy.EventArguments;

namespace Titanium.Web.Proxy.Exceptions
{
    /// <summary>
    ///     Proxy Connection exception.
    /// </summary>
    public class ProxyConnectException : ProxyException
    {
        /// <summary>
        ///     Initializes a new instance of the <see cref="ProxyConnectException" /> class.
        /// </summary>
        /// <param name="message">Message for this exception</param>
        /// <param name="innerException">Associated inner exception</param>
        /// <param name="connectEventArgs">Instance of <see cref="EventArguments.TunnelConnectSessionEventArgs" /> associated to the exception</param>
        internal ProxyConnectException(string message, Exception innerException, TunnelConnectSessionEventArgs connectEventArgs) : base(
            message, innerException)
        {
            ConnectEventArgs = connectEventArgs;
        }

        /// <summary>
        ///     Gets session info associated to the exception.
        /// </summary>
        /// <remarks>
        ///     This object properties should not be edited.
        /// </remarks>
        public TunnelConnectSessionEventArgs ConnectEventArgs { get; }
    }
}

using System;
using Titanium.Web.Proxy.EventArguments;

namespace Titanium.Web.Proxy.Exceptions
{
    /// <summary>
    /// Proxy HTTP exception
    /// </summary>
    public class ProxyHttpException : ProxyException
    {
        /// <summary>
        /// Instantiate new instance
        /// </summary>
        /// <param name="message">Message for this exception</param>
        /// <param name="innerException">Associated inner exception</param>
        /// <param name="sessionInfo">Instance of <see cref="SessionEventArgs"/> associated to the exception</param>
        public ProxyHttpException(string message, Exception innerException, SessionEventArgs sessionInfo) : base(message, innerException)
        {
            SessionInfo = sessionInfo;
        }

        /// <summary>
        /// Gets session info associated to the exception
        /// </summary>
        /// <remarks>
        /// This object should not be edited
        /// </remarks>
        public SessionEventArgs SessionInfo { get; }
    }
}
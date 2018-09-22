using System;
using Titanium.Web.Proxy.EventArguments;

namespace Titanium.Web.Proxy.Exceptions
{
    /// <summary>
    ///     Proxy HTTP exception.
    /// </summary>
    public class ProxyHttpException : ProxyException
    {
        /// <summary>
        ///     Initializes a new instance of the <see cref="ProxyHttpException" /> class.
        /// </summary>
        /// <param name="message">Message for this exception</param>
        /// <param name="innerException">Associated inner exception</param>
        /// <param name="sessionEventArgs">Instance of <see cref="EventArguments.SessionEventArgs" /> associated to the exception</param>
        internal ProxyHttpException(string message, Exception innerException, SessionEventArgs sessionEventArgs) : base(
            message, innerException)
        {
            SessionEventArgs = sessionEventArgs;
        }

        /// <summary>
        ///     Gets session info associated to the exception.
        /// </summary>
        /// <remarks>
        ///     This object properties should not be edited.
        /// </remarks>
        public SessionEventArgs SessionEventArgs { get; }
    }
}

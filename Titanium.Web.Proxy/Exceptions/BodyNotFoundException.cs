using System;

namespace Titanium.Web.Proxy.Exceptions
{
    /// <summary>
    /// An expception thrown when body is unexpectedly empty
    /// </summary>
    public class BodyNotFoundException : ProxyException
    {
        public BodyNotFoundException(string message)
            : base(message)
        {
        }
    }
}
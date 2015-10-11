using System;

namespace Titanium.Web.Proxy.Exceptions
{
    public class BodyNotFoundException : Exception
    {
        public BodyNotFoundException(string message)
            : base(message)
        {
        }
    }
}
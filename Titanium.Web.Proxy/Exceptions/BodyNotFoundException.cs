namespace Titanium.Web.Proxy.Exceptions
{
    /// <summary>
    /// An expception thrown when body is unexpectedly empty
    /// </summary>
    public class BodyNotFoundException : ProxyException
    {
        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="message"></param>
        public BodyNotFoundException(string message) : base(message)
        {
        }
    }
}

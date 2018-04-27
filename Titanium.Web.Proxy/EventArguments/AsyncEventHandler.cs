using System.Threading.Tasks;

namespace Titanium.Web.Proxy.EventArguments
{
    /// <summary>
    /// A generic asynchronous event handler used by this proxy.
    /// </summary>
    /// <typeparam name="TEventArgs"></typeparam>
    /// <param name="sender">The proxy server instance.</param>
    /// <param name="e">The event arguments.</param>
    /// <returns></returns>
    public delegate Task AsyncEventHandler<in TEventArgs>(object sender, TEventArgs e);
}

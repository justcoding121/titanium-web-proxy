using System.Threading.Tasks;

namespace Titanium.Web.Proxy.EventArguments
{
    public delegate Task AsyncEventHandler<TEventArgs>(object sender, TEventArgs e);
}
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.EventArguments
{
    public delegate Task AsyncEventHandler<in TEventArgs>(object sender, TEventArgs e);
}

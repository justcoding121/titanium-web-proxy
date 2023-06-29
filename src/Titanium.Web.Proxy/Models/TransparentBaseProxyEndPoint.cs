using System.Net;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;

namespace Titanium.Web.Proxy.Models;

public abstract class TransparentBaseProxyEndPoint : ProxyEndPoint
{
    protected TransparentBaseProxyEndPoint(IPAddress ipAddress, int port, bool decryptSsl) : base(ipAddress, port,
        decryptSsl)
    {
    }

    /// <summary>
    ///     The hostname of the generic certificate to negotiate SSL.
    ///     This will be only used when Sever Name Indication (SNI) is not supported by client,
    ///     or when it does not indicate any host name.
    /// </summary>
    public abstract string GenericCertificateName { get; set; }

    /// <summary>
    ///     The hostname (or IP-address) of the fixed forwarding remote server.
    /// </summary>
    public abstract string OverrideForwardHostName { get; set; }

    /// <summary>
    ///     The port of the fixed forwarding remote server.
    /// </summary>
    public abstract int OverrideForwardPort { get; set; }

    internal abstract Task InvokeBeforeSslAuthenticate(ProxyServer proxyServer,
        BeforeSslAuthenticateEventArgs connectArgs, ExceptionHandler? exceptionFunc);
}
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Cli.Xds;

internal sealed class XdsHostHandle : IAsyncDisposable
{
    private readonly object _host;

    private XdsHostHandle(object host) => _host = host;

    public static XdsHostHandle? TryStart(
        XdsConfig config,
        TwpConfig bootstrap,
        string stateDirectory,
        Action<TwpConfig> onConfigReady,
        CancellationToken cancellationToken)
    {
        var host = XdsAssemblyLoader.TryCreateHost(config, bootstrap, stateDirectory, onConfigReady);
        if (host is null)
        {
            return null;
        }

        var handle = new XdsHostHandle(host);
        host.GetType().GetMethod("Start")!.Invoke(host, [cancellationToken]);
        return handle;
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
    }
}

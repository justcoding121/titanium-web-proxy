using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Cli.Xds;

internal sealed class XdsHostHandle : IAsyncDisposable
{
    private readonly object _host;
    private readonly Action<TwpConfig> _onConfigReady;

    private XdsHostHandle(object host, Action<TwpConfig> onConfigReady)
    {
        _host = host;
        _onConfigReady = onConfigReady;
    }

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

        var handle = new XdsHostHandle(host, onConfigReady);
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

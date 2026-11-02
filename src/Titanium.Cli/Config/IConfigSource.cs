using Titanium.Cli.Parsers;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Cli.Config;

/// <summary>Supplies the effective Titanium config (file, xDS, or a future source).</summary>
internal interface IConfigSource
{
    bool IsDynamic { get; }

    Task<TwpConfig> LoadAsync(CancellationToken cancellationToken);
}

internal sealed class FileConfigSource(string path) : IConfigSource
{
    public bool IsDynamic => false;

    public Task<TwpConfig> LoadAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var loaded = ConfigLoader.Load(path);
        return Task.FromResult(loaded.Config);
    }
}

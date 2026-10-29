using System.Text;
using Titanium.Web.Proxy.Abstractions.Plugins;

namespace Titanium.Cli.Config;

/// <summary>
/// Writes manager-pushed YAML to the active config path, then runs the same atomic reload as SIGHUP /
/// dashboard reload. Safe under the reload gate: a failed reload leaves the prior in-memory config
/// (and the previous file is not restored — operators recover via last-known-good / re-publish).
/// </summary>
internal sealed class CliManagedConfigApplier : IConfigApplier
{
    private readonly string _configPath;
    private readonly Func<CancellationToken, Task<PlusReloadResult>> _reload;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CliManagedConfigApplier(string configPath, Func<CancellationToken, Task<PlusReloadResult>> reload)
    {
        _configPath = configPath;
        _reload = reload;
    }

    public async Task<ConfigApplyResult> ApplyAsync(
        string yaml,
        string configHash,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(_configPath));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tmp = _configPath + "." + Environment.ProcessId + ".mgr.tmp";
            await File.WriteAllTextAsync(tmp, yaml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
                .ConfigureAwait(false);
            File.Move(tmp, _configPath, overwrite: true);

            var result = await _reload(cancellationToken).ConfigureAwait(false);
            if (!result.Ok)
            {
                return new ConfigApplyResult
                {
                    Ok = false,
                    Error = result.Error ?? "reload failed",
                };
            }

            return new ConfigApplyResult
            {
                Ok = true,
                AppliedHash = result.ConfigHash ?? configHash,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new ConfigApplyResult { Ok = false, Error = ex.Message };
        }
        finally
        {
            _gate.Release();
        }
    }
}

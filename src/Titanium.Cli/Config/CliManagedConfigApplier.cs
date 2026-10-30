using System.Text;
using Titanium.Cli.Parsers;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Configuration;

namespace Titanium.Cli.Config;

/// <summary>
/// Writes manager-pushed YAML to a temp sibling, validates it, then atomically replaces the live config path
/// and runs the same reload as SIGHUP / dashboard reload. On reload failure the previous file is restored from
/// <c>.bak</c>.
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
        string? tmp = null;
        try
        {
            var fullPath = Path.GetFullPath(_configPath);
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var ext = Path.GetExtension(fullPath);
            if (string.IsNullOrEmpty(ext))
            {
                ext = ".yaml";
            }

            var bak = fullPath + ".bak";
            tmp = Path.Combine(
                dir ?? ".",
                Path.GetFileNameWithoutExtension(fullPath) + ".mgr-" + Environment.ProcessId + ext);
            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

            await File.WriteAllTextAsync(tmp, yaml, encoding, cancellationToken).ConfigureAwait(false);

            if (TryLoadAndValidate(tmp, out var loadError) is null)
            {
                return new ConfigApplyResult { Ok = false, Error = loadError };
            }

            if (File.Exists(fullPath))
            {
                File.Copy(fullPath, bak, overwrite: true);
            }

            File.Move(tmp, fullPath, overwrite: true);
            tmp = null;

            var result = await _reload(cancellationToken).ConfigureAwait(false);
            if (!result.Ok)
            {
                return RestoreAfterFailedReload(fullPath, bak, result);
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
            if (tmp is not null)
            {
                try
                {
                    File.Delete(tmp);
                }
                catch
                {
                    /* ignore */
                }
            }

            _gate.Release();
        }
    }

    private static LoadedConfig? TryLoadAndValidate(string path, out string? error)
    {
        LoadedConfig loaded;
        try
        {
            loaded = ConfigLoader.Load(path);
        }
        catch (Exception ex)
        {
            error = "invalid config: " + ex.Message;
            return null;
        }

        var errors = TwpConfigValidator.Validate(loaded.Config);
        if (errors.Count > 0)
        {
            error = string.Join("; ", errors);
            return null;
        }

        error = null;
        return loaded;
    }

    private static ConfigApplyResult RestoreAfterFailedReload(string fullPath, string bak, PlusReloadResult result)
    {
        if (File.Exists(bak))
        {
            try
            {
                File.Copy(bak, fullPath, overwrite: true);
            }
            catch (Exception restoreEx) when (restoreEx is IOException or UnauthorizedAccessException)
            {
                return new ConfigApplyResult
                {
                    Ok = false,
                    Error = (result.Error ?? "reload failed") + "; restore failed: " + restoreEx.Message,
                };
            }
        }

        return new ConfigApplyResult
        {
            Ok = false,
            Error = result.Error ?? "reload failed",
        };
    }
}

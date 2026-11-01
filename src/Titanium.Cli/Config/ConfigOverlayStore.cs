namespace Titanium.Cli.Config;

/// <summary>Atomic read/write of the persisted config overlay file.</summary>
public sealed class ConfigOverlayStore
{
    private readonly string _path;
    private readonly object _lock = new();

    public ConfigOverlayStore(string path) => _path = path;

    public string Path => _path;

    public bool Exists => File.Exists(_path);

    public ConfigOverlayDocument LoadOrEmpty()
    {
        lock (_lock)
        {
            if (!File.Exists(_path))
            {
                return new ConfigOverlayDocument();
            }

            return ConfigOverlay.Deserialize(File.ReadAllText(_path));
        }
    }

    public void Save(ConfigOverlayDocument overlay)
    {
        var json = ConfigOverlay.Serialize(overlay);
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        lock (_lock)
        {
            var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temp, json);
                File.Move(temp, _path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
        }
    }

    public void Delete()
    {
        lock (_lock)
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
    }
}

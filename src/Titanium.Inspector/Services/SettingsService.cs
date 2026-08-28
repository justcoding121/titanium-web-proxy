using System.Text.Json;
using System.Text.Json.Serialization;

namespace Titanium.Inspector.Services;

public sealed class InspectorSettings
{
    public bool CheckForUpdatesOnStartup { get; set; }
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }
    public string UpdateChannel { get; set; } = "Stable";
}

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public SettingsService(string? path = null)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TitaniumInspector");
        Directory.CreateDirectory(dir);
        _path = path ?? Path.Combine(dir, "settings.json");
        Current = LoadFromDisk();
    }

    public InspectorSettings Current { get; private set; }

    public static SettingsService Load() => new();

    public void Save()
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(Current, JsonOptions));
    }

    private InspectorSettings LoadFromDisk()
    {
        if (!File.Exists(_path))
        {
            return new InspectorSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<InspectorSettings>(File.ReadAllText(_path), JsonOptions)
                   ?? new InspectorSettings();
        }
        catch
        {
            return new InspectorSettings();
        }
    }
}

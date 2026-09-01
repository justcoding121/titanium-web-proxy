namespace Titanium.Inspector.Services;

/// <summary>Retention knobs for long-running Inspector capture.</summary>
public sealed class SessionStoreOptions
{
    public int MaxSessionsInMemory { get; set; } = 10_000;
    public long MaxCaptureBytesInMemory { get; set; } = 512L * 1024 * 1024;
    public int HotBodySessions { get; set; } = 2_000;
    public bool SpillBodiesToDisk { get; set; } = true;
    public long DiskCacheMaxBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public int DiskCacheMaxAgeDays { get; set; } = 7;

    public static SessionStoreOptions FromSettings(InspectorSettings settings) =>
        new()
        {
            MaxSessionsInMemory = settings.MaxSessionsInMemory > 0 ? settings.MaxSessionsInMemory : 10_000,
            MaxCaptureBytesInMemory = settings.MaxCaptureBytesInMemory > 0
                ? settings.MaxCaptureBytesInMemory
                : 512L * 1024 * 1024,
            HotBodySessions = settings.HotBodySessions > 0 ? settings.HotBodySessions : 2_000,
            SpillBodiesToDisk = settings.SpillBodiesToDisk,
            DiskCacheMaxBytes = settings.DiskCacheMaxBytes > 0
                ? settings.DiskCacheMaxBytes
                : 2L * 1024 * 1024 * 1024,
            DiskCacheMaxAgeDays = settings.DiskCacheMaxAgeDays > 0 ? settings.DiskCacheMaxAgeDays : 7,
        };
}

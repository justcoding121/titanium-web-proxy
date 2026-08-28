using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Services;

/// <summary>HAR + native session archive zip import/export.</summary>
public static class SessionArchive
{
    public static async Task ExportHarAsync(IEnumerable<SessionSnapshot> sessions, string path, CancellationToken ct = default)
    {
        var entries = sessions.Select(s => new
        {
            startedDateTime = s.StartedUtc.UtcDateTime.ToString("o"),
            request = new
            {
                method = s.Method,
                url = s.Url,
                headers = ParseHeaders(s.RequestHeadersText),
                bodySize = s.RequestBodyBytes?.Length ?? s.RequestBodyText?.Length ?? -1,
            },
            response = new
            {
                status = s.StatusCode ?? 0,
                headers = ParseHeaders(s.ResponseHeadersText),
                bodySize = s.ResponseBodyBytes?.Length ?? s.ResponseBodyText?.Length ?? -1,
            },
        }).ToList();

        var har = new { log = new { version = "1.2", creator = new { name = "Titanium Inspector", version = "7.0.0" }, entries } };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(har, new JsonSerializerOptions { WriteIndented = true }), ct);
    }

    public static async Task ExportNativeArchiveAsync(IEnumerable<SessionSnapshot> sessions, string zipPath, CancellationToken ct = default)
    {
        await using var fs = File.Create(zipPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var index = 0;
        foreach (var session in sessions)
        {
            ct.ThrowIfCancellationRequested();
            var entry = zip.CreateEntry($"session-{index:D5}.json");
            await using var stream = entry.Open();
            await JsonSerializer.SerializeAsync(stream, session, cancellationToken: ct);
            index++;
        }
    }

    public static async Task<List<SessionSnapshot>> ImportNativeArchiveAsync(string zipPath, CancellationToken ct = default)
    {
        var list = new List<SessionSnapshot>();
        await using var fs = File.OpenRead(zipPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries.OrderBy(e => e.FullName))
        {
            ct.ThrowIfCancellationRequested();
            if (!entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            await using var stream = entry.Open();
            var snap = await JsonSerializer.DeserializeAsync<SessionSnapshot>(stream, cancellationToken: ct);
            if (snap is not null)
            {
                list.Add(snap);
            }
        }

        return list;
    }

    private static object[] ParseHeaders(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line =>
            {
                var i = line.IndexOf(':');
                return i <= 0
                    ? new { name = line, value = "" }
                    : new { name = line[..i].Trim(), value = line[(i + 1)..].Trim() };
            })
            .Cast<object>()
            .ToArray();
    }
}

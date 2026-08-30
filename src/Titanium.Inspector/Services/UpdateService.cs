using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace Titanium.Inspector.Services;

public sealed class UpdateCheckResult
{
    public bool UpdateAvailable { get; init; }
    public string Message { get; init; } = "";
    public string? RemoteVersion { get; init; }
}

/// <summary>GitHub Releases updater for Stable/Beta channels.</summary>
public sealed class UpdateService
{
    private const string GitHubReleasesUrl =
        "https://api.github.com/repos/justcoding121/titanium-web-proxy/releases"; // NOSONAR S1075 -- Official GitHub Releases API endpoint.

    private const string GitHubLatestReleaseUrl =
        "https://api.github.com/repos/justcoding121/titanium-web-proxy/releases/latest"; // NOSONAR S1075 -- Official GitHub Releases API endpoint.

    private readonly SettingsService _settings;

    public UpdateService(SettingsService settings) => _settings = settings;

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        _settings.Current.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
        _settings.Save();

        var channel = _settings.Current.UpdateChannel;
        var local = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TitaniumInspector", "7.0"));

            string json;
            if (channel.Equals("Beta", StringComparison.OrdinalIgnoreCase))
            {
                json = await http.GetStringAsync(GitHubReleasesUrl, cancellationToken);
                using var arr = JsonDocument.Parse(json);
                foreach (var el in arr.RootElement.EnumerateArray())
                {
                    if (el.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
                    {
                        return Compare(local, el.GetProperty("tag_name").GetString());
                    }
                }

                return new UpdateCheckResult { Message = "No beta release found." };
            }

            json = await http.GetStringAsync(GitHubLatestReleaseUrl, cancellationToken);
            using var doc = JsonDocument.Parse(json);
            return Compare(local, doc.RootElement.GetProperty("tag_name").GetString());
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult { Message = $"Update check failed: {ex.Message}" };
        }
    }

    private static UpdateCheckResult Compare(Version local, string? tag)
    {
        var remoteText = tag?.TrimStart('v') ?? "0.0.0";
        if (!Version.TryParse(remoteText.Split('-')[0], out var remote))
        {
            remote = new Version(0, 0);
        }

        if (remote > local)
        {
            return new UpdateCheckResult
            {
                UpdateAvailable = true,
                RemoteVersion = remoteText,
                Message = $"Update available: {remoteText}",
            };
        }

        return new UpdateCheckResult
        {
            RemoteVersion = remoteText,
            Message = "Titanium Inspector is up to date.",
        };
    }
}

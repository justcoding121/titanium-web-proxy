using System.Text;
using System.Text.Json;

namespace Titanium.Inspector.DesktopProbe;

public sealed class ProbeLog : IDisposable
{
    private readonly string _resultsDir;
    private readonly string _logPath;
    private readonly List<ProbeStep> _steps = new();
    private readonly object _gate = new();

    public ProbeLog(string? resultsDir = null)
    {
        _resultsDir = resultsDir ?? Path.Combine(
            Path.GetDirectoryName(typeof(ProbeLog).Assembly.Location) ?? Environment.CurrentDirectory,
            "..", "..", "..", "results");
        _resultsDir = Path.GetFullPath(_resultsDir);
        Directory.CreateDirectory(_resultsDir);
        _logPath = Path.Combine(_resultsDir, $"probe-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
    }

    public string ResultsDir => _resultsDir;
    public string LastRunJsonPath => Path.Combine(_resultsDir, "last-run.json");

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    public void Step(string name, bool ok, string detail = "")
    {
        lock (_gate)
            _steps.Add(new ProbeStep(name, ok, detail, DateTime.UtcNow));
        Write(ok ? "PASS" : "FAIL", $"{name}: {detail}");
    }

    public void WriteSummary(string command, int exitCode)
    {
        var summary = new ProbeSummary(
            command,
            exitCode,
            Environment.OSVersion.VersionString,
            DateTime.UtcNow,
            _steps.ToList());
        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(LastRunJsonPath, json);
        Info($"Wrote {LastRunJsonPath}");
    }

    private void Write(string level, string message)
    {
        var line = $"{DateTime.UtcNow:O} [{level}] {message}";
        Console.WriteLine(line);
        lock (_gate)
            File.AppendAllText(_logPath, line + Environment.NewLine, Encoding.UTF8);
    }

    public void Dispose()
    {
        // nothing pooled
    }

    private sealed record ProbeStep(string Name, bool Ok, string Detail, DateTime Utc);
    private sealed record ProbeSummary(
        string Command,
        int ExitCode,
        string Os,
        DateTime Utc,
        IReadOnlyList<ProbeStep> Steps);
}

using System.Text;
using System.Text.Json;

namespace Titanium.Cli.QaProbe;

public sealed class ProbeLog : IDisposable
{
    private readonly string _resultsDir;
    private readonly List<ProbeStep> _steps = new();
    private readonly object _gate = new();

    public ProbeLog(string? resultsDir = null)
    {
        _resultsDir = resultsDir ?? ResolveResultsDir();
        Directory.CreateDirectory(_resultsDir);
    }

    public string ResultsDir => _resultsDir;
    public string LastRunJsonPath => Path.Combine(_resultsDir, "last-run.json");
    public IReadOnlyList<ProbeStep> Steps => _steps;

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);

    public void Step(string name, bool ok, string detail = "", bool skipped = false)
    {
        lock (_gate)
            _steps.Add(new ProbeStep(name, ok, skipped, detail, DateTime.UtcNow));
        Write(skipped ? "SKIP" : ok ? "PASS" : "FAIL", $"{name}: {detail}");
    }

    public void WriteSummary(string command, int exitCode)
    {
        var summary = new ProbeSummary(command, exitCode, Environment.OSVersion.VersionString, DateTime.UtcNow, _steps.ToList());
        File.WriteAllText(LastRunJsonPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        Info($"Wrote {LastRunJsonPath}");
    }

    private void Write(string level, string message)
    {
        var line = $"{DateTime.UtcNow:O} [{level}] {message}";
        Console.WriteLine(line);
        lock (_gate)
            File.AppendAllText(Path.Combine(_resultsDir, $"probe-{DateTime.UtcNow:yyyyMMdd}.log"), line + Environment.NewLine, Encoding.UTF8);
    }

    private static string ResolveResultsDir()
    {
        var probeDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        if (File.Exists(Path.Combine(probeDir, "CliQaProbe.csproj")))
            return Path.Combine(probeDir, "results");
        return Path.Combine(Path.GetTempPath(), "ti-cli-qa-probe");
    }

    public void Dispose() { }

    public sealed record ProbeStep(string Name, bool Ok, bool Skipped, string Detail, DateTime Utc);
    private sealed record ProbeSummary(string Command, int ExitCode, string Os, DateTime Utc, IReadOnlyList<ProbeStep> Steps);
}

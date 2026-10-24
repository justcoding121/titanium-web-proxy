using System.Globalization;
using System.Text;
using System.Text.Json;
using Titanium.Web.Proxy.EventArguments;

namespace Titanium.Cli.AccessLog;

/// <summary>Appends one JSON object per completed session (opt-in; null writer = no-op).</summary>
public sealed class JsonAccessLogWriter : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly StreamWriter _writer;
    private readonly double _sampleRate;
    private readonly object _gate = new();

    public JsonAccessLogWriter(string path, double sampleRate = 1.0)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _writer = new StreamWriter(
            new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
        _sampleRate = sampleRate <= 0 ? 0 : Math.Clamp(sampleRate, 0, 1);
    }

    public static string FormatRecord(
        string? method,
        string? url,
        string? host,
        int status,
        double? durationMs,
        string? clientIp,
        string? route = null,
        string? cluster = null,
        string? destination = null,
        string? error = null,
        string? requestId = null,
        string? traceId = null)
    {
        var record = new Dictionary<string, object?>
        {
            ["ts"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["method"] = method,
            ["url"] = url,
            ["host"] = host,
            ["status"] = status,
            ["durationMs"] = durationMs.HasValue ? Math.Round(durationMs.Value, 3) : null,
            ["clientIp"] = clientIp,
        };
        AddIfPresent(record, "route", route);
        AddIfPresent(record, "cluster", cluster);
        AddIfPresent(record, "destination", destination);
        AddIfPresent(record, "error", error);
        AddIfPresent(record, "requestId", requestId);
        AddIfPresent(record, "traceId", traceId);
        return JsonSerializer.Serialize(record, JsonOptions);
    }

    private static void AddIfPresent(Dictionary<string, object?> record, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            record[name] = value;
        }
    }

    public static string FormatLine(SessionEventArgs session)
    {
        var req = session.HttpClient.Request;
        var resp = session.HttpClient.Response;
        var durationMs = session.Timing?.TotalDuration.TotalMilliseconds;
        return FormatRecord(
            req.Method,
            req.RequestUri?.ToString() ?? req.Url,
            req.Host ?? req.RequestUri?.Host,
            resp.StatusCode,
            durationMs,
            session.ClientRemoteEndPoint.Address.ToString(),
            session.MatchedRoute?.Id,
            session.MatchedRoute?.ClusterId,
            session.UpstreamDestinationId,
            session.ForwarderError == Titanium.Web.Proxy.Abstractions.Diagnostics.ForwarderError.None
                ? null
                : session.ForwarderError.ToString(),
            session.ProxyRequestId,
            session.ProxyTraceId);
    }

    public void TryWrite(SessionEventArgs session)
    {
        if (!ShouldSample())
        {
            return;
        }

        WriteLine(FormatLine(session));
    }

    public void TryWriteRecord(
        string? method,
        string? url,
        string? host,
        int status,
        double? durationMs,
        string? clientIp)
    {
        if (!ShouldSample())
        {
            return;
        }

        WriteLine(FormatRecord(method, url, host, status, durationMs, clientIp));
    }

    private bool ShouldSample()
    {
        if (_sampleRate <= 0)
        {
            return false;
        }

        return _sampleRate >= 1.0 || Random.Shared.NextDouble() <= _sampleRate;
    }

    private void WriteLine(string line)
    {
        lock (_gate)
        {
            _writer.WriteLine(line);
        }
    }

    public void Dispose() => _writer.Dispose();
}

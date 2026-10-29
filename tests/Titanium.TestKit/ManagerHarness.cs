using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Titanium.TestKit;

/// <summary>
/// Minimal in-process stand-in for a Plus manager: serves a long-poll config endpoint and accepts ack/nack.
/// Enough for unit tests; expand when manager push lands.
/// </summary>
public sealed class ManagerHarness : IAsyncDisposable
{
    private HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<AckRecord> _acks = new();
    private readonly object _configGate = new();
    private Task? _loop;
    private string _configJson = """{"version":1,"policies":[]}""";
    private long _configVersion = 1;

    public string BaseUrl { get; private set; } = "";

    public IReadOnlyCollection<AckRecord> Acks => _acks.ToArray();

    public sealed record AckRecord(string NodeId, long Version, bool Accepted, string? Reason);

    public static async Task<ManagerHarness> StartAsync()
    {
        var harness = new ManagerHarness();
        Exception? last = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var port = FreePort();
            harness._listener = new HttpListener();
            harness._listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                harness._listener.Start();
                harness.BaseUrl = $"http://127.0.0.1:{port}/";
                harness._loop = Task.Run(harness.AcceptLoopAsync);
                await Task.Yield();
                return harness;
            }
            catch (Exception ex) when (ex is HttpListenerException or SocketException)
            {
                last = ex;
                try { harness._listener.Close(); } catch { /* ignore */ }
            }
        }

        throw new InvalidOperationException("Failed to bind ManagerHarness HttpListener.", last);
    }

    /// <summary>Replaces the config payload and bumps the version so long-poll waiters wake.</summary>
    public void PublishConfig(object config)
    {
        lock (_configGate)
        {
            _configVersion++;
            _configJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["version"] = _configVersion,
                ["config"] = config,
            });
        }
    }

    public long ConfigVersion
    {
        get
        {
            lock (_configGate)
            {
                return _configVersion;
            }
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException or OperationCanceledException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (string.Equals(path, "/v1/config", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            {
                await LongPollConfigAsync(context).ConfigureAwait(false);
                return;
            }

            // Agent-shaped aliases matching titanium-manager (/v1/agent/config, /v1/agent/ack).
            if (string.Equals(path, "/v1/agent/config", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            {
                await LongPollAgentConfigAsync(context).ConfigureAwait(false);
                return;
            }

            if (string.Equals(path, "/v1/ack", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                await ReadAckAsync(context).ConfigureAwait(false);
                return;
            }

            if (string.Equals(path, "/v1/agent/ack", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                await ReadAgentAckAsync(context).ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = 404;
            context.Response.Close();
        }
        catch
        {
            try { context.Response.Abort(); } catch { /* ignore */ }
        }
    }

    private async Task LongPollConfigAsync(HttpListenerContext context)
    {
        var since = 0L;
        var raw = context.Request.QueryString["since"];
        if (!string.IsNullOrEmpty(raw))
        {
            _ = long.TryParse(raw, out since);
        }

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline && !_stop.IsCancellationRequested)
        {
            string body;
            long version;
            lock (_configGate)
            {
                version = _configVersion;
                body = _configJson;
            }

            if (version > since)
            {
                var bytes = Encoding.UTF8.GetBytes(body);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                context.Response.Close();
                return;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        context.Response.StatusCode = 204;
        context.Response.Close();
    }

    private async Task LongPollAgentConfigAsync(HttpListenerContext context)
    {
        // Map have=<hash> to since=version: empty/missing have means "give current"; matching hash waits.
        var have = context.Request.QueryString["have"] ?? "";
        var waitRaw = context.Request.QueryString["wait"];
        var waitSec = 2;
        if (int.TryParse(waitRaw, out var w))
        {
            waitSec = Math.Clamp(w, 0, 30);
        }

        var deadline = DateTime.UtcNow.AddSeconds(waitSec);
        while (DateTime.UtcNow < deadline && !_stop.IsCancellationRequested)
        {
            string body;
            long version;
            lock (_configGate)
            {
                version = _configVersion;
                body = _configJson;
            }

            var hash = version.ToString();
            if (string.IsNullOrEmpty(have) || !string.Equals(have, hash, StringComparison.Ordinal))
            {
                // Shape close to titanium-manager agent response.
                var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["revisionId"] = version,
                    ["hash"] = hash,
                    ["configHash"] = hash,
                    ["yaml"] = body,
                    ["version"] = version,
                    ["config"] = JsonSerializer.Deserialize<JsonElement>(body),
                });
                var bytes = Encoding.UTF8.GetBytes(payload);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                context.Response.Close();
                return;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        context.Response.StatusCode = 204;
        context.Response.Close();
    }

    private async Task ReadAckAsync(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var json = await reader.ReadToEndAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var root = doc.RootElement;
        var nodeId = root.TryGetProperty("nodeId", out var n) ? n.GetString() ?? "" : "";
        var version = root.TryGetProperty("version", out var v) && v.TryGetInt64(out var ver) ? ver : 0;
        var accepted = !root.TryGetProperty("accepted", out var a) || a.ValueKind != JsonValueKind.False;
        var reason = root.TryGetProperty("reason", out var r) ? r.GetString() : null;
        _acks.Enqueue(new AckRecord(nodeId, version, accepted, reason));
        context.Response.StatusCode = 204;
        context.Response.Close();
    }

    private async Task ReadAgentAckAsync(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var json = await reader.ReadToEndAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var root = doc.RootElement;
        var nodeId = root.TryGetProperty("node_id", out var n) ? n.GetString() ?? ""
            : root.TryGetProperty("nodeId", out var n2) ? n2.GetString() ?? "" : "";
        var version = 0L;
        if (root.TryGetProperty("config_hash", out var h) && long.TryParse(h.GetString(), out var fromHash))
        {
            version = fromHash;
        }
        else if (root.TryGetProperty("version", out var v) && v.TryGetInt64(out var ver))
        {
            version = ver;
        }

        var accepted = !root.TryGetProperty("ok", out var o) || o.ValueKind != JsonValueKind.False;
        var reason = root.TryGetProperty("error", out var e) ? e.GetString() : null;
        _acks.Enqueue(new AckRecord(nodeId, version, accepted, reason));
        context.Response.StatusCode = 204;
        context.Response.Close();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try { _listener.Stop(); } catch { /* ignore */ }
        try { _listener.Close(); } catch { /* ignore */ }
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { /* ignore */ }
        }

        _stop.Dispose();
    }
}

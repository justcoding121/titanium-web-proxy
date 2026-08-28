using System.Net;
using System.Text;

namespace Titanium.E2E.Tests.Harness;

/// <summary>Minimal HTTP origin for ForwardHost / route E2E.</summary>
public sealed class EchoOrigin : IDisposable
{
    private readonly HttpListener _listener = new();
    private CancellationTokenSource? _cts;

    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}/";

    public EchoOrigin(int? port = null)
    {
        Port = port ?? CliProcessHarness.GetFreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
            // ignore
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().WaitAsync(ct);
            }
            catch
            {
                return;
            }

            _ = Task.Run(() => Handle(ctx), ct);
        }
    }

    private static void Handle(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            var body = Encoding.UTF8.GetBytes($"echo:{path}:{ctx.Request.HttpMethod}");
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/plain";
            ctx.Response.ContentLength64 = body.Length;
            ctx.Response.OutputStream.Write(body);
            ctx.Response.Close();
        }
        catch
        {
            try { ctx.Response.Abort(); } catch { /* ignore */ }
        }
    }
}

/// <summary>Writes temp twp.yaml configs for CLI E2E.</summary>
public static class ConfigFixtures
{
    public static string WriteForwardHost(string dir, int listenPort, int originPort)
    {
        var path = Path.Combine(dir, $"fwd-{listenPort}.yaml");
        File.WriteAllText(path, $"""
            schemaVersion: "7.0"
            listeners:
              - host: "127.0.0.1"
                port: {listenPort}
                decryptSsl: false
                forwardHost: "127.0.0.1"
                forwardPort: {originPort}
            """);
        return path;
    }

    public static string WriteRoutes(string dir, int listenPort, int originPort)
    {
        var path = Path.Combine(dir, $"routes-{listenPort}.json");
        File.WriteAllText(path, $$"""
            {
              "schemaVersion": "7.0",
              "listeners": [
                { "host": "127.0.0.1", "port": {{listenPort}}, "decryptSsl": false }
              ],
              "routes": [
                {
                  "id": "r1",
                  "clusterId": "c1",
                  "order": 1,
                  "match": { "path": "/", "pathKind": "Prefix" }
                }
              ],
              "clusters": [
                {
                  "id": "c1",
                  "algorithm": "RoundRobin",
                  "destinations": [
                    { "id": "d1", "address": "127.0.0.1", "port": {{originPort}} }
                  ]
                }
              ]
            }
            """);
        return path;
    }

    public static string WriteStatic(string dir, int listenPort, string staticRoot)
    {
        var path = Path.Combine(dir, $"static-{listenPort}.yaml");
        File.WriteAllText(path, $"""
            schemaVersion: "7.0"
            listeners:
              - host: "127.0.0.1"
                port: {listenPort}
                decryptSsl: false
            staticFiles:
              root: "{staticRoot.Replace("\\", "/")}"
              enableGzip: true
            """);
        return path;
    }

    public static string WritePlus(string dir, int listenPort, int originPort, int controlPort, string secret)
    {
        var path = Path.Combine(dir, $"plus-{listenPort}.yaml");
        File.WriteAllText(path, $"""
            schemaVersion: "7.0"
            listeners:
              - host: "127.0.0.1"
                port: {listenPort}
                decryptSsl: false
                forwardHost: "127.0.0.1"
                forwardPort: {originPort}
            plus:
              enabled: true
              controlPlane:
                host: "127.0.0.1"
                port: {controlPort}
                sharedSecret: "{secret}"
              options:
                cache.enable: "true"
            """);
        return path;
    }

    public static string WriteTls(string dir, int listenPort, int originPort, string certPath, string keyPath)
    {
        var path = Path.Combine(dir, $"tls-{listenPort}.yaml");
        File.WriteAllText(path, $"""
            schemaVersion: "7.0"
            listeners:
              - host: "127.0.0.1"
                port: {listenPort}
                decryptSsl: true
                forwardHost: "127.0.0.1"
                forwardPort: {originPort}
            certificates:
              certificatePath: "{certPath.Replace("\\", "/")}"
              privateKeyPath: "{keyPath.Replace("\\", "/")}"
            """);
        return path;
    }

    public static string WriteExplicitMitm(
        string dir,
        int listenPort,
        string logFilePath,
        bool plus = false,
        int controlPort = 0,
        string? secret = null)
    {
        var path = Path.Combine(dir, plus ? $"plus-mitm-{listenPort}.yaml" : $"mitm-{listenPort}.yaml");
        var log = logFilePath.Replace("\\", "/");
        var plusBlock = plus
            ? $"""
            plus:
              enabled: true
              controlPlane:
                host: "127.0.0.1"
                port: {controlPort}
                sharedSecret: "{secret}"
            """
            : "";
        File.WriteAllText(path, $"""
            schemaVersion: "7.0"
            listeners:
              - host: "127.0.0.1"
                port: {listenPort}
                decryptSsl: true
            logging:
              enabled: true
              minimumLevel: Debug
              enableConsole: true
              enableFile: true
              filePath: "{log}"
            {plusBlock}
            """);
        return path;
    }

    public static string WriteInvalid(string dir)
    {
        var path = Path.Combine(dir, "invalid.yaml");
        File.WriteAllText(path, "listeners:\n  - port: -1\n");
        return path;
    }
}

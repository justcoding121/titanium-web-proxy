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

    public static string WriteSiteFile(string dir, int listenPort, int originPort)
    {
        // Site-file dialect has no listen directive — CLI defaults to explicit :8000.
        // Use a free-port YAML wrapper is not the dialect; for process E2E we validate
        // parse via `test` and run with an absolute listen via companion yaml is wrong.
        // Instead write site-file that maps host→origin and rely on default listener when
        // listenPort == 8000; callers should pass GetFreePort only for unique temp names.
        var path = Path.Combine(dir, $"site-{listenPort}.twp");
        File.WriteAllText(path, $"127.0.0.1 / => http://127.0.0.1:{originPort}\n");
        return path;
    }

    public static string WriteHttpServerConf(string dir, int listenPort, int originPort)
    {
        var path = Path.Combine(dir, $"http-{listenPort}.conf");
        File.WriteAllText(path, $$"""
            listen {{listenPort}};
            server_name localhost;
            location / {
              proxy_pass http://127.0.0.1:{{originPort}};
            }
            """);
        return path;
    }

    public static string WriteLogging(string dir, int listenPort, int originPort, string logFile)
    {
        var path = Path.Combine(dir, $"log-{listenPort}.yaml");
        File.WriteAllText(path, $"""
            schemaVersion: "7.0"
            listeners:
              - host: "127.0.0.1"
                port: {listenPort}
                decryptSsl: false
                forwardHost: "127.0.0.1"
                forwardPort: {originPort}
            logging:
              enabled: true
              minimumLevel: Information
              enableConsole: true
              enableFile: true
              filePath: "{logFile.Replace("\\", "/")}"
            """);
        return path;
    }

    public static string WriteListenerFlags(string dir, int listenPort, int originPort, bool enableHttp2, bool enableHttp3)
    {
        var path = Path.Combine(dir, $"flags-{listenPort}.yaml");
        // EnableHttp3 requires DecryptSsl on transparent endpoints.
        File.WriteAllText(path, $"""
            schemaVersion: "7.0"
            listeners:
              - host: "127.0.0.1"
                port: {listenPort}
                decryptSsl: {enableHttp3.ToString().ToLowerInvariant()}
                forwardHost: "127.0.0.1"
                forwardPort: {originPort}
                enableHttp2: {enableHttp2.ToString().ToLowerInvariant()}
                enableHttp3: {enableHttp3.ToString().ToLowerInvariant()}
            """);
        return path;
    }

    public static string WritePlusOptions(
        string dir,
        int listenPort,
        int originPort,
        int controlPort,
        string secret,
        Dictionary<string, string> options,
        bool useRoutes = false)
    {
        var path = Path.Combine(dir, $"twp-plus-opts-{listenPort}.json");
        var optsJson = string.Join(",\n", options.Select(kv =>
            $"      \"{kv.Key}\": \"{kv.Value.Replace("\\", "\\\\")}\""));
        var listener = useRoutes
            ? $$"""{ "host": "127.0.0.1", "port": {{listenPort}}, "decryptSsl": false }"""
            : $$"""{ "host": "127.0.0.1", "port": {{listenPort}}, "decryptSsl": false, "forwardHost": "127.0.0.1", "forwardPort": {{originPort}} }""";
        var routesBlock = useRoutes
            ? $$"""
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
              ],
            """
            : "";
        File.WriteAllText(path, $$"""
            {
              "schemaVersion": "7.0",
              "listeners": [ {{listener}} ],
              {{routesBlock}}
              "plus": {
                "enabled": true,
                "controlPlane": {
                  "host": "127.0.0.1",
                  "port": {{controlPort}},
                  "sharedSecret": "{{secret}}"
                },
                "options": {
            {{optsJson}}
                }
              }
            }
            """);
        return path;
    }

    public static string WritePlusRoutes(string dir, int listenPort, int originPort, int controlPort, string secret)
    {
        var path = Path.Combine(dir, $"twp-plus-routes-{listenPort}.json");
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
              ],
              "plus": {
                "enabled": true,
                "controlPlane": {
                  "host": "127.0.0.1",
                  "port": {{controlPort}},
                  "sharedSecret": "{{secret}}"
                }
              }
            }
            """);
        return path;
    }

    public static string WriteAcmeNoDirectory(string dir, int listenPort, int originPort)
    {
        var path = Path.Combine(dir, $"acme-{listenPort}.yaml");
        File.WriteAllText(path, $"""
            schemaVersion: "7.0"
            listeners:
              - host: "127.0.0.1"
                port: {listenPort}
                decryptSsl: true
                forwardHost: "127.0.0.1"
                forwardPort: {originPort}
            certificates:
              acmeEmail: "test@example.com"
              acmeDomain: "example.test"
            """);
        return path;
    }
}

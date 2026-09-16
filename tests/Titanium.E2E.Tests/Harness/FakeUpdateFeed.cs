using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Titanium.E2E.Tests.Harness;

/// <summary>
/// Loopback update feed for <c>TITANIUM_UPDATE_FEED</c>: serves a release-manifest JSON,
/// RID CLI zip, and Plus DLL with matching SHA256.
/// </summary>
public sealed class FakeUpdateFeed : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly byte[] _cliZip;
    private readonly byte[] _plusDll;
    private readonly string _cliSha;
    private readonly string _plusSha;
    private readonly string _rid;
    private readonly string _version;
    private readonly string _channel;

    public int Port { get; }
    public string ManifestUrl => $"http://127.0.0.1:{Port}/manifest.json";
    public string Version => _version;
    public string Rid => _rid;

    public FakeUpdateFeed(
        string sourceCliDirectory,
        string? plusDllPath,
        string version = "99.0.0",
        string channel = "stable")
    {
        _version = version;
        _channel = channel;
        _rid = SuggestRid();
        _cliZip = BuildCliZip(sourceCliDirectory);
        _cliSha = Convert.ToHexString(SHA256.HashData(_cliZip)).ToLowerInvariant();

        if (!string.IsNullOrEmpty(plusDllPath) && File.Exists(plusDllPath))
        {
            _plusDll = File.ReadAllBytes(plusDllPath);
        }
        else
        {
            // Minimal placeholder when Plus is not needed for the test
            _plusDll = Encoding.UTF8.GetBytes("fake-plus-dll-placeholder");
        }

        _plusSha = Convert.ToHexString(SHA256.HashData(_plusDll)).ToLowerInvariant();

        (_listener, Port) = CliProcessHarness.BindHttpListenerOrRetry(p => $"http://127.0.0.1:{p}/");
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void Dispose()
    {
        _cts.Cancel();
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

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            if (path.Equals("/manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(ctx, BuildManifestJson());
                return;
            }

            if (path.Equals($"/cli-{_rid}.zip", StringComparison.OrdinalIgnoreCase))
            {
                WriteBytes(ctx, _cliZip, "application/zip");
                return;
            }

            if (path.Equals("/Titanium.Plus.dll", StringComparison.OrdinalIgnoreCase))
            {
                WriteBytes(ctx, _plusDll, "application/octet-stream");
                return;
            }

            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
        }
        catch
        {
            try { ctx.Response.Abort(); } catch { /* ignore */ }
        }
    }

    private string BuildManifestJson()
    {
        var doc = new
        {
            version = _version,
            channel = _channel,
            products = new
            {
                cli = new
                {
                    assets = new Dictionary<string, object>
                    {
                        [_rid] = new
                        {
                            url = $"http://127.0.0.1:{Port}/cli-{_rid}.zip",
                            sha256 = _cliSha,
                        },
                    },
                },
                plus = new
                {
                    version = _version,
                    asset = new
                    {
                        url = $"http://127.0.0.1:{Port}/Titanium.Plus.dll",
                        sha256 = _plusSha,
                    },
                },
            },
        };
        return JsonSerializer.Serialize(doc);
    }

    private static byte[] BuildCliZip(string sourceCliDirectory)
    {
        var zipPath = Path.Combine(Path.GetTempPath(), "twp-feed-cli-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            System.IO.Compression.ZipFile.CreateFromDirectory(
                sourceCliDirectory,
                zipPath,
                System.IO.Compression.CompressionLevel.Fastest,
                includeBaseDirectory: false);
            return File.ReadAllBytes(zipPath);
        }
        finally
        {
            try { File.Delete(zipPath); } catch { /* ignore */ }
        }
    }

    private static string SuggestRid()
    {
        if (OperatingSystem.IsWindows())
        {
            return "win-x64";
        }

        if (OperatingSystem.IsMacOS())
        {
            return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        }

        // Linux
        var musl = File.Exists("/etc/alpine-release") ||
                   (File.Exists("/lib/libc.musl-x86_64.so.1") || File.Exists("/lib/libc.musl-aarch64.so.1"));
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        return musl ? $"linux-musl-{arch}" : $"linux-{arch}";
    }

    private static void WriteJson(HttpListenerContext ctx, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }

    private static void WriteBytes(HttpListenerContext ctx, byte[] bytes, string contentType)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }
}

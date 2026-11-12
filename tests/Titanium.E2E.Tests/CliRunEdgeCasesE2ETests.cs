using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;

namespace Titanium.E2E.Tests;

/// <summary>
/// Edge-case CLI run behaviors: missing / invalid config exits before binding,
/// and corrupt Plus bundles from a fake feed are rejected without touching existing files.
/// </summary>
[TestClass]
public class CliRunEdgeCasesE2ETests
{
    // ── invalid / missing config ──────────────────────────────────────────────

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Run_MissingConfigFlag_ExitsNonZero()
    {
        using var harness = new CliProcessHarness();
        var (code, stdout, stderr) = await harness.RunOnceAsync(
            ["run"],
            timeout: TimeSpan.FromSeconds(30));

        Assert.AreNotEqual(0, code, stdout + stderr);
        var combined = stdout + stderr;
        Assert.IsTrue(
            combined.Contains("-c", StringComparison.Ordinal) ||
            combined.Contains("config", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Missing", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("required", StringComparison.OrdinalIgnoreCase),
            combined);
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Run_NonExistentConfigFile_ExitsNonZeroWithoutBinding()
    {
        var missing = Path.Combine(Path.GetTempPath(), "twp-e2e-nofile-" + Guid.NewGuid().ToString("N") + ".yaml");
        Assert.IsFalse(File.Exists(missing), "Pre-condition: file must not exist");

        using var harness = new CliProcessHarness();
        var (code, stdout, stderr) = await harness.RunOnceAsync(
            ["run", "-c", missing],
            timeout: TimeSpan.FromSeconds(30));

        Assert.AreNotEqual(0, code, $"Expected non-zero exit; stdout={stdout} stderr={stderr}");
        var combined = stdout + stderr;
        Assert.IsTrue(
            combined.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("No such", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains(Path.GetFileName(missing), StringComparison.OrdinalIgnoreCase),
            combined);
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Run_InvalidYamlConfig_ExitsNonZeroWithoutBinding()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-e2e-badyaml-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "twp.yaml");
        // Write YAML that cannot be parsed as a TwpConfig (missing quote terminator).
        await File.WriteAllTextAsync(path, "listeners:\n  - port: [\n");
        try
        {
            using var harness = new CliProcessHarness();
            var (code, stdout, stderr) = await harness.RunOnceAsync(
                ["run", "-c", path],
                timeout: TimeSpan.FromSeconds(30));

            Assert.AreNotEqual(0, code, $"Expected non-zero exit; stdout={stdout} stderr={stderr}");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Run_SchemaValidationError_ExitsNonZeroWithoutBinding()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-e2e-badschema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "twp.yaml");
        // Write a valid YAML structure that fails schema validation:
        // schemaVersion is intentionally wrong to ensure the validator fires.
        await File.WriteAllTextAsync(path, """
            listeners:
              - port: 99999999
                host: "127.0.0.1"
            """);
        try
        {
            CliProcessHarness.AssertLoopbackConfig(path);
            using var harness = new CliProcessHarness();
            var (code, stdout, stderr) = await harness.RunOnceAsync(
                ["run", "-c", path],
                timeout: TimeSpan.FromSeconds(40));

            // Port 99999999 is invalid; the validator must reject it (exit 1) before any listen.
            Assert.AreNotEqual(0, code, $"Expected non-zero exit; stdout={stdout} stderr={stderr}");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    // ── corrupt Plus bundle ───────────────────────────────────────────────────

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Update_Plus_ZipSlipBundle_FromFakeFeed_IsRejected_FilesUntouched()
    {
        using var harness = CliProcessHarness.CreateIsolatedCopy(copyPlus: false);
        var dir = harness.CliDirectory;

        // Build the corrupt zip in a scratch directory completely separate from the CLI directory.
        var scratch = Path.Combine(Path.GetTempPath(), "twp-e2e-slip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {

        // Record exactly which files existed before the update attempt.
        var before = SnapshotFiles(dir);

        // Build a zip bundle whose sole entry uses a path-traversal name (zip-slip attack).
        var zipSlipZip = BuildZipWithEntry(scratch, "../evil.dll", "evil-payload");
        var zipHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(zipSlipZip))).ToLowerInvariant();

        using var feed = new MinimalFakeFeed(dir, zipSlipZip, zipHash);
        var env = new Dictionary<string, string?> { ["TITANIUM_UPDATE_FEED"] = feed.ManifestUrl };

        var (code, stdout, stderr) = await harness.RunOnceAsync(
            ["update", "--plus"],
            timeout: TimeSpan.FromMinutes(2),
            env: env);

        // CLI must exit non-zero; the invalid entry must not escape the bundle directory.
        Assert.AreNotEqual(0, code, stdout + stderr);
        Assert.IsFalse(File.Exists(Path.Combine(dir, "..", "evil.dll")),
            "Zip-slip entry must not be extracted to the parent");
        Assert.IsFalse(File.Exists(Path.Combine(dir, "evil.dll")),
            "Zip-slip payload must not land in the install directory");

        // Original files must be untouched.
        CollectionAssert.AreEqual(before, SnapshotFiles(dir));

        } // end scratch try
        finally { TryDelete(scratch); }
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Update_Plus_BadHashBundle_FromFakeFeed_IsRejected_FilesUntouched()
    {
        using var harness = CliProcessHarness.CreateIsolatedCopy(copyPlus: false);
        var dir = harness.CliDirectory;
        var before = SnapshotFiles(dir);

        var scratch = Path.Combine(Path.GetTempPath(), "twp-e2e-badhash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {

        // Build a valid-looking Plus bundle zip in the scratch dir.
        var plusZip = BuildValidPlusZip(scratch);
        // Serve a manifest whose SHA256 does NOT match the zip.
        var wrongHash = new string('0', 64); // all-zero hash that can never match the real file

        using var feed = new MinimalFakeFeed(dir, plusZip, wrongHash);
        var env = new Dictionary<string, string?> { ["TITANIUM_UPDATE_FEED"] = feed.ManifestUrl };

        var (code, stdout, stderr) = await harness.RunOnceAsync(
            ["update", "--plus"],
            timeout: TimeSpan.FromMinutes(2),
            env: env);

        Assert.AreNotEqual(0, code, stdout + stderr);
        Assert.IsFalse(File.Exists(Path.Combine(dir, "Titanium.Plus.dll")),
            "Plus must not be installed when the bundle hash does not match");

        // Original files must be untouched.
        CollectionAssert.AreEqual(before, SnapshotFiles(dir));

        } // end scratch try
        finally { TryDelete(scratch); }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static string[] SnapshotFiles(string directory) =>
        Directory.GetFiles(directory)
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Cast<string>()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

    /// <summary>Builds a zip whose only entry has the given (possibly unsafe) name.</summary>
    private static string BuildZipWithEntry(string baseDir, string entryName, string content)
    {
        var path = Path.Combine(baseDir, "corrupt-bundle-" + Guid.NewGuid().ToString("N") + ".zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry(entryName);
            using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
            w.Write(content);
        }

        return path;
    }

    /// <summary>
    /// Builds a minimal valid Plus bundle zip (one DLL + file list) for hash-mismatch tests.
    /// </summary>
    private static string BuildValidPlusZip(string baseDir)
    {
        var path = Path.Combine(baseDir, "plus-bundle-" + Guid.NewGuid().ToString("N") + ".zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var dll = zip.CreateEntry("Titanium.Plus.dll");
            using (var w = new StreamWriter(dll.Open(), Encoding.UTF8))
            {
                w.Write("fake-plus-placeholder");
            }

            var list = zip.CreateEntry("Titanium.Plus.files");
            using var lw = new StreamWriter(list.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            lw.WriteLine("Titanium.Plus.dll");
            lw.WriteLine("Titanium.Plus.files");
        }

        return path;
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // ignore
        }
    }

    // ── MinimalFakeFeed ───────────────────────────────────────────────────────

    /// <summary>
    /// A minimal update feed that serves a single manifest pointing to one Plus bundle zip
    /// (with a caller-supplied SHA256 that may intentionally be wrong).
    /// </summary>
    private sealed class MinimalFakeFeed : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly byte[] _zipBytes;
        private readonly string _zipSha;
        private readonly string _zipFileName;

        public string ManifestUrl { get; }

        public MinimalFakeFeed(string cliDirectory, string zipPath, string zipSha)
        {
            _zipBytes = File.ReadAllBytes(zipPath);
            _zipSha = zipSha;
            _zipFileName = Path.GetFileName(zipPath);

            (_listener, var port) = CliProcessHarness.BindHttpListenerOrRetry(p => $"http://127.0.0.1:{p}/");
            ManifestUrl = $"http://127.0.0.1:{port}/manifest.json";
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); _listener.Close(); } catch { /* ignore */ }
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync().WaitAsync(ct); }
                catch { return; }
                _ = Task.Run(() => Handle(ctx), ct);
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            try
            {
                var p = ctx.Request.Url?.AbsolutePath ?? "/";
                if (p.Equals("/manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    WriteJson(ctx, BuildManifest());
                    return;
                }

                if (p.Equals("/" + _zipFileName, StringComparison.OrdinalIgnoreCase))
                {
                    WriteBytes(ctx, _zipBytes, "application/zip");
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

        private string BuildManifest()
        {
            var manifest = new
            {
                version = "99.0.0",
                channel = "stable",
                products = new
                {
                    cli = new { assets = new Dictionary<string, object>() },
                    plus = new
                    {
                        version = "99.0.0",
                        bundle = new
                        {
                            url = ManifestUrl.Replace("manifest.json", _zipFileName),
                            sha256 = _zipSha,
                            size = _zipBytes.Length,
                        },
                        asset = new
                        {
                            url = ManifestUrl.Replace("manifest.json", _zipFileName),
                            sha256 = _zipSha,
                        },
                    },
                },
            };
            return JsonSerializer.Serialize(manifest);
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
}

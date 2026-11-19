using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Titanium.Inspector.DesktopProbe.Shared;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.InstallSmoke;

internal static class SmokeApp
{
    private static readonly int[] ForbiddenPorts = [8000, 8866, 9080, 9464];
    private static readonly JsonSerializerOptions SettingsJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("commands: wipe | fresh | upgrade-seed | upgrade-verify | cli | local-windows");
            return 2;
        }

        var cmd = args[0].ToLowerInvariant();
        var opt = Options.Parse(args.Skip(1).ToArray());
        try
        {
            return cmd switch
            {
                "wipe" => Wipe(opt),
                "fresh" => await FreshAsync(opt),
                "upgrade-seed" => await UpgradeSeedAsync(opt),
                "upgrade-verify" => await UpgradeVerifyAsync(opt),
                "cli" => await CliAsync(opt),
                "local-windows" => await LocalWindowsAsync(opt),
                _ => Fail(2, "unknown command " + cmd),
            };
        }
        catch (Exception ex)
        {
            DumpDiagnostics(opt.Out);
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static async Task<int> LocalWindowsAsync(Options opt)
    {
        if (!OperatingSystem.IsWindows())
            return Fail(2, "local-windows is the isolated Windows run.");
        if (Process.GetProcessesByName("TitaniumInspector").Length > 0)
            return Fail(2, "Titanium Inspector is already running. Quit it before the isolated smoke. This run will not kill it.");

        var prevExe = Require(opt.PrevExe, "--prev-exe");
        var exe = Require(opt.Exe, "--exe");
        using var guard = WindowsProfileGuard.Capture();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            guard.Restore();
            Environment.Exit(2);
        };

        var browsers = opt.Browsers ?? ["firefox", "chrome", "edge", "brave", "opera"];
        var code = 0;
        try
        {
            guard.ParkProfile();
            code = await UpgradeSeedAsync(opt with { Exe = prevExe, Browsers = ["firefox"] });
            if (code == 0)
                code = await UpgradeVerifyAsync(opt with { Exe = exe, Browsers = browsers });
            if (code == 0)
            {
                Wipe(opt);
                code = await FreshAsync(opt with { Exe = exe, Browsers = browsers });
            }
        }
        catch (Exception ex)
        {
            DumpDiagnostics(opt.Out);
            Console.Error.WriteLine(ex);
            code = 1;
        }
        finally
        {
            guard.Restore();
        }

        if (code == 0 && guard.IsClean())
        {
            Console.WriteLine("LOCAL ISOLATED RUN CLEAN");
            return 0;
        }

        if (code == 0)
            return Fail(1, "profile or proxy was not restored");
        return code;
    }

    private static async Task<int> UpgradeSeedAsync(Options opt)
    {
        var exe = Require(opt.Exe, "--exe");
        var port = FreePort();
        WriteFreshSettings(port, decrypt: false);
        var app = await StartAndDecryptAsync(exe, port, expectAlreadyOn: false);
        try
        {
            var thumb = ThumbFromPfx();
            var firefox = BrowserPaths.Resolve("firefox") ?? throw new InvalidOperationException("firefox is missing");
            if (!await ProveAsync(app.StartedUtc, firefox, "firefox", ["https://example.com/"]))
                return Fail(1, "upgrade seed firefox did not decrypt example.com");
            await app.QuitAsync();
            var outDir = opt.Out ?? Path.GetTempPath();
            Directory.CreateDirectory(outDir);
            await File.WriteAllTextAsync(Path.Combine(outDir, "root-thumb.txt"), thumb);
            Console.WriteLine("upgrade-seed thumb=" + thumb);
            return 0;
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    private static async Task<int> UpgradeVerifyAsync(Options opt)
    {
        var exe = Require(opt.Exe, "--exe");
        var port = ReadBindPort();
        var app = await InspectorProcess.StartAsync(exe, port);
        try
        {
            if (!await ReadDecryptAsync())
                return Fail(1, "upgraded build cleared decryptHttps");
            var thumb = ThumbFromPfx();
            var expected = opt.ExpectThumb ?? ReadThumbFile(opt.Out);
            if (!string.IsNullOrEmpty(expected) && !string.Equals(expected, thumb, StringComparison.OrdinalIgnoreCase))
                return Fail(1, $"root thumbprint changed from {expected} to {thumb}");
            if (!await app.CaptureIsListeningAsync())
                return Fail(1, "capture is not listening after upgrade");
            if (!await WaitProxyAsync(port, TimeSpan.FromSeconds(20)))
                return Fail(1, "system proxy is off after upgrade. " + ProxySnapshot() + " " + LinuxProxyDiag(port));
            Console.WriteLine("upgrade-verify decrypt=true thumb=" + thumb + " proxy=on");
            return await ProveBrowsersAsync(app.StartedUtc, opt.Browsers ?? DefaultBrowsers());
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    private static async Task<int> FreshAsync(Options opt)
    {
        var exe = Require(opt.Exe, "--exe");
        var port = FreePort();
        WriteFreshSettings(port, decrypt: false);
        exe = await MoveMacDmgAppIfNeededAsync(exe);
        var app = await InspectorProcess.StartAsync(exe, port);
        try
        {
            if (await ReadDecryptAsync())
                return Fail(1, "fresh install started with decryptHttps true");
            if (!await app.CaptureIsListeningAsync())
                return Fail(1, "fresh install did not start capture");
            if (!await WaitProxyAsync(port, TimeSpan.FromSeconds(20)))
                return Fail(1, "fresh install did not enable the system proxy. " + ProxySnapshot());
            app = await DecryptAndMaybeRelaunchAsync(app, exe, port);
            return await ProveBrowsersAsync(app.StartedUtc, opt.Browsers ?? DefaultBrowsers());
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    private static int Wipe(Options opt)
    {
        var profile = ProfileDir();
        var local = LocalDir();
        TryDeleteDir(profile);
        TryDeleteDir(local);
        if (!string.IsNullOrEmpty(opt.Thumb) && OperatingSystem.IsWindows())
            WindowsProfileGuard.RemoveUserRoot(opt.Thumb);
        Console.WriteLine("wipe profile");
        return 0;
    }

    private static async Task<string> MoveMacDmgAppIfNeededAsync(string exe)
    {
        if (!OperatingSystem.IsMacOS() || !exe.Contains("/Volumes/", StringComparison.Ordinal))
            return exe;

        var mover = await InspectorProcess.StartAsync(exe, 0);
        try
        {
            var move = await UiClicks.ClickMoveToApplicationsAsync(mover.Pid, TimeSpan.FromSeconds(20));
            if (!move.Ok)
            {
                Console.WriteLine("click: FALLBACK (ditto to Applications)");
                await mover.QuitAsync();
                // Contents/MacOS -> the .app bundle is two levels up.
                var bundle = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(exe)!, "..", ".."));
                var copied = RunCapture("ditto", $"\"{bundle}\" \"/Applications/Titanium Inspector.app\"", 60000);
                Console.WriteLine("ditto " + bundle + " exit=" + copied.Code);
                if (copied.Code != 0 && !string.IsNullOrWhiteSpace(copied.Output))
                    Console.WriteLine(TrimOneLine(copied.Output));
            }
            else
            {
                var deadline = DateTime.UtcNow.AddSeconds(40);
                while (!mover.HasExited && DateTime.UtcNow < deadline)
                    await Task.Delay(300);
            }
        }
        finally
        {
            await mover.DisposeAsync();
        }

        foreach (var root in new[]
                 {
                     "/Applications",
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications"),
                 })
        {
            var installed = Path.Combine(root, "Titanium Inspector.app", "Contents", "MacOS", "TitaniumInspector");
            if (File.Exists(installed))
                return installed;
        }

        throw new InvalidOperationException("Move to Applications did not install Titanium Inspector.app");
    }

    private static async Task<int> CliAsync(Options opt)
    {
        var titanium = Require(opt.Titanium, "--titanium");
        RequireCliVersion(titanium);
        var port = FreePort();
        var dir = opt.Out ?? Path.Combine(Path.GetTempPath(), "titanium-cli-smoke");
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "access.ndjson");
        var yaml = Path.Combine(dir, "mitm.yaml");
        await File.WriteAllTextAsync(yaml, $$"""
            schemaVersion: "7.0"
            listeners:
              - host: "127.0.0.1"
                port: {{port}}
                decryptSsl: true
            server:
              accessLog:
                path: "{{log.Replace("\\", "/")}}"
            """);
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = titanium,
            Arguments = "run --config \"" + yaml + "\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("titanium did not start");
        try
        {
            if (!await WaitTcpAsync(port, TimeSpan.FromSeconds(30)))
                return Fail(1, "titanium run did not listen");
            var pem = ExportCliRootPem(dir);
            var curl = OperatingSystem.IsWindows() ? "curl.exe" : "curl";
            var curlArgs = OperatingSystem.IsWindows()
                ? $"--silent --show-error --ssl-no-revoke --max-time 30 --proxy http://127.0.0.1:{port} --cacert \"{pem}\" https://example.com/"
                : $"--silent --show-error --max-time 30 --proxy http://127.0.0.1:{port} --cacert \"{pem}\" https://example.com/";
            var curlResult = RunCapture(curl, curlArgs, 35000);
            Console.WriteLine("curl exit=" + curlResult.Code + " " + TrimOneLine(curlResult.Output));
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(log) && ReadSharedText(log).Contains("https://example.com", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("cli decrypted https://example.com");
                    return 0;
                }
                await Task.Delay(300);
            }

            Console.WriteLine("access log: " + TrimOneLine(File.Exists(log) ? ReadSharedText(log) : "(missing)"));
            return Fail(1, "access log has no decrypted https://example.com");
        }
        finally
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(5000);
                }
            }
            catch { /* already gone */ }
        }
    }

    private static async Task<int> ProveBrowsersAsync(DateTime startedUtc, string[] browsers)
    {
        foreach (var name in browsers)
        {
            var path = BrowserPaths.Resolve(name);
            if (path is null)
                return Fail(1, name + " is not installed");
            var safari = name.Equals("safari", StringComparison.OrdinalIgnoreCase);
            if (safari)
            {
                // Chrome already proved the root. A later Safari visit to the same origin
                // can still skip the proxy on UDP/443 after the upgrade pass learned HTTP/3.
                await TrustRootAsync();
                await Task.Delay(1500);
                BlockOutboundQuic();
            }

            try
            {
                var urls = name.Equals("firefox", StringComparison.OrdinalIgnoreCase) && browsers.Length == 1 && false
                    ? new[] { "https://example.com/" }
                    : new[] { "https://example.com/", "https://www.google.com/" };
                if (!await ProveAsync(startedUtc, path, name, urls))
                    return Fail(1, name + " did not produce a decrypted GET");
                Console.WriteLine("pass " + name);
            }
            finally
            {
                if (safari)
                    RestorePacketFilter();
            }
        }

        return 0;
    }

    private static async Task<bool> ProveAsync(DateTime startedUtc, string browserPath, string name, string[] urls)
    {
        foreach (var url in urls)
        {
            var before = DateTime.UtcNow;
            using var launched = LaunchBrowser(name, browserPath, url);
            var safari = name.Equals("safari", StringComparison.OrdinalIgnoreCase);
            var ok = await WaitForHarAsync(before, url, safari ? TimeSpan.FromSeconds(75) : TimeSpan.FromSeconds(60), () =>
            {
                Console.WriteLine("relaunch " + name + " " + url);
                if (OperatingSystem.IsLinux() && LinuxDesktopId(name) is not null)
                {
                    KillLinuxFamily(name);
                    TryGtkLaunch(name, url);
                }
                else if (safari)
                {
                    TryRelaunchSafari(url);
                }
                else if (OperatingSystem.IsMacOS()
                         && !name.Equals("firefox", StringComparison.OrdinalIgnoreCase))
                    TryStartAgain(browserPath, url);
            }, safari ? TimeSpan.FromSeconds(8) : null);
            launched.Kill();
            if (!ok)
            {
                PrintRecentHarEntries(before);
                DumpDiagnostics(null);
                Console.WriteLine($"no decrypted GET for {url} via {name}");
                return false;
            }
        }

        return true;
    }

    private static BrowserLaunch LaunchBrowser(string name, string path, string url)
    {
        if (name.Equals("safari", StringComparison.OrdinalIgnoreCase))
        {
            SystemProxyBrowserCapture.KillSafariProcesses();
            SystemProxyBrowserCapture.StartSafari(url, fresh: true);
            return new BrowserLaunch(null, safari: true);
        }

        if (name.Equals("firefox", StringComparison.OrdinalIgnoreCase))
        {
            var profile = SystemProxyBrowserCapture.CreateTempFirefoxProfile("twp-smoke-ff-");
            File.AppendAllText(Path.Combine(profile, "user.js"),
                "user_pref(\"security.enterprise_roots.enabled\", true);\n");
            Console.WriteLine("firefox: seeded enterprise_roots");
            if (OperatingSystem.IsLinux())
            {
                var port = ReadBindPort();
                File.AppendAllText(Path.Combine(profile, "user.js"),
                    "user_pref(\"network.proxy.type\", 1);\n" +
                    "user_pref(\"network.proxy.http\", \"127.0.0.1\");\n" +
                    "user_pref(\"network.proxy.http_port\", " + port + ");\n" +
                    "user_pref(\"network.proxy.ssl\", \"127.0.0.1\");\n" +
                    "user_pref(\"network.proxy.ssl_port\", " + port + ");\n" +
                    "user_pref(\"network.proxy.share_proxy_settings\", true);\n");
                Console.WriteLine("firefox: manual proxy prefs for the Linux temp profile");
                ImportRootIntoFirefoxProfile(profile);
            }
            var proc = SystemProxyBrowserCapture.StartFirefox(path, profile, url);
            return new BrowserLaunch(proc, safari: false, profile);
        }

        var dir = Path.Combine(Path.GetTempPath(), "twp-smoke-" + name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        if (OperatingSystem.IsLinux())
        {
            var desktop = LinuxDesktopId(name);
            if (desktop is not null)
            {
                KillLinuxFamily(name);
                Environment.SetEnvironmentVariable(
                    "TITANIUM_SMOKE_BROWSER_FLAGS",
                    "--new-window --disable-component-update --disable-background-networking");
                Console.WriteLine("launch " + name + " via " + desktop);
                PrintDesktopExec(desktop);
                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "gtk-launch",
                    ArgumentList = { desktop, url },
                    UseShellExecute = false,
                }) ?? throw new InvalidOperationException("gtk-launch did not start " + name);
                Thread.Sleep(2500);
                PrintMatchingProcessArgs(name);
                PrintLinuxProxyHooks();
                ProbeProxyWithCurl();
                return new BrowserLaunch(proc, safari: false, dir, LinuxKillName(name));
            }
        }

        var killName = OperatingSystem.IsMacOS() ? MacKillName(name) : null;
        var chromium = SystemProxyBrowserCapture.StartChromiumViaSystemProxy(path, dir, url, disableQuic: true);
        return new BrowserLaunch(chromium, safari: false, dir, killName);
    }

    private static void KillLinuxFamily(string name)
    {
        foreach (var proc in LinuxKillNames(name))
            RunCapture("killall", proc, 4000);
        Thread.Sleep(400);
    }

    private static IEnumerable<string> LinuxKillNames(string name)
    {
        var primary = LinuxKillName(name);
        if (primary is not null)
            yield return primary;
        switch (name.ToLowerInvariant())
        {
            case "chrome":
                yield return "google-chrome";
                yield return "google-chrome-stable";
                yield break;
            case "edge":
                yield return "microsoft-edge";
                yield return "microsoft-edge-stable";
                yield break;
            case "brave":
                yield return "brave-browser";
                yield break;
        }
    }

    private static string? LinuxKillName(string name) => name.ToLowerInvariant() switch
    {
        "chrome" => "chrome",
        "edge" => "msedge",
        "brave" => "brave",
        "opera" => "opera",
        _ => null,
    };

    private static string? LinuxDesktopId(string name)
    {
        var files = name.ToLowerInvariant() switch
        {
            "chrome" => new[] { "google-chrome.desktop", "google-chrome-stable.desktop" },
            "edge" => new[] { "microsoft-edge.desktop", "microsoft-edge-stable.desktop" },
            "brave" => new[] { "brave-browser.desktop" },
            "opera" => new[] { "opera.desktop" },
            _ => null,
        };
        if (files is null)
            return null;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var file in files)
        {
            var path = Path.Combine(home, ".local", "share", "applications", file);
            if (!File.Exists(path))
                continue;
            return Path.GetFileNameWithoutExtension(path);
        }

        Console.WriteLine("desktop missing for " + name);
        return null;
    }

    private static void PrintDesktopExec(string desktopId)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = Path.Combine(home, ".local", "share", "applications", desktopId + ".desktop");
        if (!File.Exists(path))
            return;
        var printedScript = false;
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith("DBusActivatable=", StringComparison.Ordinal))
                Console.WriteLine("desktop " + line);
            if (!line.StartsWith("Exec=", StringComparison.Ordinal))
                continue;
            Console.WriteLine("desktop " + line);
            if (printedScript)
                continue;
            var script = line["Exec=".Length..].Trim().Split(' ', 2)[0].Trim('"');
            if (!script.EndsWith(".sh", StringComparison.Ordinal) || !File.Exists(script))
                continue;
            printedScript = true;
            Console.WriteLine("launcher " + script);
            foreach (var scriptLine in File.ReadLines(script))
                Console.WriteLine("launcher " + scriptLine);
        }
    }

    private static void PrintLinuxProxyHooks()
    {
        try
        {
            var policy = "/etc/opt/chrome/policies/managed/titanium-inspector-proxy.json";
            Console.WriteLine(File.Exists(policy)
                ? "machine-policy " + TrimOneLine(File.ReadAllText(policy))
                : "machine-policy missing");
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var prefs = Path.Combine(home, ".config", "google-chrome", "Default", "Preferences");
            Console.WriteLine(File.Exists(prefs)
                ? "chrome-prefs " + TrimOneLine(File.ReadAllText(prefs))
                : "chrome-prefs missing");
            var mode = RunCapture("gsettings", "get org.gnome.system.proxy mode", 8000);
            Console.WriteLine("gsettings-mode " + TrimOneLine(mode.Output));
        }
        catch (Exception ex)
        {
            Console.WriteLine("proxy-hooks failed: " + ex.Message);
        }
    }

    private static void ProbeProxyWithCurl()
    {
        try
        {
            var port = ReadBindPort();
            var curl = RunCapture(
                "curl",
                "-sS -o /dev/null -w %{http_code} --proxy http://127.0.0.1:" + port + " --max-time 15 https://example.com/",
                20000);
            Console.WriteLine("proxy-probe curl exit=" + curl.Code + " http=" + TrimOneLine(curl.Output));
        }
        catch (Exception ex)
        {
            Console.WriteLine("proxy-probe failed: " + ex.Message);
        }
    }

    private static void PrintMatchingProcessArgs(string name)
    {
        try
        {
            var probe = Process.Start(new ProcessStartInfo
            {
                FileName = "ps",
                ArgumentList = { "-eo", "pid,args" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (probe is null)
                return;
            var text = probe.StandardOutput.ReadToEnd();
            probe.WaitForExit(3000);
            foreach (var line in text.Split('\n'))
            {
                if (line.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("titanium-inspector/launchers", StringComparison.Ordinal))
                    Console.WriteLine("argv " + line.Trim());
            }

            if (OperatingSystem.IsLinux())
            {
                foreach (var line in text.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (!trimmed.Contains("/opt/google/chrome/chrome", StringComparison.Ordinal) ||
                        trimmed.Contains("--type=", StringComparison.Ordinal))
                        continue;
                    var pid = trimmed.Split(' ', 2)[0];
                    var envPath = "/proc/" + pid + "/environ";
                    if (!File.Exists(envPath))
                        continue;
                    foreach (var item in System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(envPath)).Split('\0'))
                    {
                        if (item.Contains("proxy", StringComparison.OrdinalIgnoreCase))
                            Console.WriteLine("chrome-env " + item);
                    }
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("argv probe failed: " + ex.Message);
        }
    }

    private static string? MacKillName(string name) => name.ToLowerInvariant() switch
    {
        "chrome" => "Google Chrome",
        "edge" => "Microsoft Edge",
        "brave" => "Brave Browser",
        "opera" => "Opera",
        "firefox" => "firefox",
        _ => null,
    };

    private static void ImportRootIntoFirefoxProfile(string profile)
    {
        var pfx = Path.Combine(ProfileDir(), "rootCert.pfx");
        if (!File.Exists(pfx))
            return;
        var pem = Path.Combine(profile, "smoke-root.pem");
        File.WriteAllText(pem, ExportPem(pfx));
        Run("certutil", $"-N -d sql:{profile} --empty-password", 15000);
        Run("certutil", $"-d sql:{profile} -A -t \"C,,\" -n \"Titanium Smoke Root\" -i \"{pem}\"", 15000);
        Console.WriteLine("firefox: imported root into the temp profile");
    }

    private static void PrintRecentHarEntries(DateTime notBeforeUtc)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TitaniumInspector",
            "session-cache");
        if (!Directory.Exists(root))
            return;
        foreach (var file in Directory.EnumerateFiles(root, "*.har", SearchOption.AllDirectories))
        {
            DateTime write;
            try { write = File.GetLastWriteTimeUtc(file); }
            catch { continue; }
            if (write < notBeforeUtc)
                continue;
            string text;
            try { text = ReadSharedText(file); }
            catch { continue; }
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (!doc.RootElement.TryGetProperty("log", out var log) || !log.TryGetProperty("entries", out var entries))
                    continue;
                foreach (var entry in entries.EnumerateArray())
                {
                    var method = entry.TryGetProperty("request", out var req) && req.TryGetProperty("method", out var m) ? m.GetString() : "?";
                    var entryUrl = reqUrl(entry);
                    var status = entry.TryGetProperty("response", out var resp) && resp.TryGetProperty("status", out var st) ? st.GetInt32() : 0;
                    var reason = "";
                    if (entry.TryGetProperty("_inspector", out var ext) && ext.TryGetProperty("opaqueReason", out var opaque))
                        reason = opaque.GetString() ?? "";
                    Console.WriteLine($"har-entry {method} {status} {reason} {entryUrl}");
                }
            }
            catch
            {
                Console.WriteLine("har-entry unreadable " + file);
            }
        }

        static string? reqUrl(JsonElement entry) =>
            entry.TryGetProperty("request", out var req) && req.TryGetProperty("url", out var u) ? u.GetString() : null;
    }

    private static void TryStartAgain(string browserPath, string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = browserPath,
                Arguments = "--no-first-run --disable-quic --disable-component-update --disable-background-networking \"" + url + "\"",
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine("relaunch failed: " + ex.Message);
        }
    }

    private static void TryGtkLaunch(string name, string url)
    {
        var desktop = LinuxDesktopId(name);
        if (desktop is null)
            return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "gtk-launch",
                ArgumentList = { desktop, url },
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine("relaunch failed: " + ex.Message);
        }
    }

    private static async Task<bool> WaitForHarAsync(
        DateTime notBeforeUtc, string url, TimeSpan timeout, Action? nudge = null, TimeSpan? nudgeEvery = null)
    {
        var host = new Uri(url).Host;
        var deadline = DateTime.UtcNow + timeout;
        var nudgeAt = DateTime.UtcNow + (nudgeEvery ?? TimeSpan.FromSeconds(12));
        while (DateTime.UtcNow < deadline)
        {
            if (HarHasDecryptedGet(notBeforeUtc, host))
                return true;
            if (nudge is not null && DateTime.UtcNow >= nudgeAt)
            {
                nudge();
                nudgeAt = nudgeEvery is null ? DateTime.MaxValue : DateTime.UtcNow + nudgeEvery.Value;
            }

            await Task.Delay(500);
        }

        return HarHasDecryptedGet(notBeforeUtc, host);
    }

    /// <summary>
    /// Quit Safari completely and open the URL with <c>-F</c>.
    /// Re-trusting on every nudge made the window fit only two launches.
    /// </summary>
    private static void TryRelaunchSafari(string url)
    {
        SystemProxyBrowserCapture.KillSafariProcesses();
        SystemProxyBrowserCapture.StartSafari(url, fresh: true);
    }

    /// <summary>
    /// Safari has no switch to disable HTTP/3, and the feature flag is ignored.
    /// After a decrypted upgrade visit, the next load of the same origin can go
    /// out on UDP/443 and leave only a CONNECT in the HAR. Drop that traffic for
    /// the Safari proof so the GET falls back through the proxy.
    /// </summary>
    private static void BlockOutboundQuic()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var conf = Path.Combine(Path.GetTempPath(), "twp-smoke-pf.conf");
        var baseRules = File.Exists("/etc/pf.conf") ? File.ReadAllText("/etc/pf.conf") : string.Empty;
        // The runner ruleset passes outbound UDP with "quick", so a block appended
        // after it never matches. A leading quick block wins and forces the GET
        // back through the proxy on the fresh Safari visit.
        File.WriteAllText(
            conf,
            "block drop out quick inet proto udp from any to any port 443\n"
            + "block drop out quick inet6 proto udp from any to any port 443\n"
            + baseRules);
        var loaded = RunCapture("sudo", "pfctl -f \"" + conf + "\"", 15000);
        Console.WriteLine("pfctl load exit=" + loaded.Code + " " + TrimOneLine(loaded.Output));
        if (loaded.Code != 0)
            return;

        var enabled = RunCapture("sudo", "pfctl -E", 10000);
        Console.WriteLine("pfctl enable exit=" + enabled.Code + " " + TrimOneLine(enabled.Output));
    }

    private static void RestorePacketFilter()
    {
        if (!OperatingSystem.IsMacOS() || !File.Exists("/etc/pf.conf"))
            return;

        var restored = RunCapture("sudo", "pfctl -f /etc/pf.conf", 15000);
        Console.WriteLine("pfctl restore exit=" + restored.Code + " " + TrimOneLine(restored.Output));
    }

    private static bool HarHasDecryptedGet(DateTime notBeforeUtc, string host)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TitaniumInspector",
            "session-cache");
        if (!Directory.Exists(root))
            return false;
        foreach (var file in Directory.EnumerateFiles(root, "*.har", SearchOption.AllDirectories))
        {
            DateTime write;
            try { write = File.GetLastWriteTimeUtc(file); }
            catch { continue; }
            if (write < notBeforeUtc)
                continue;
            string text;
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                text = reader.ReadToEnd();
            }
            catch { continue; }
            if (EntryMatches(text, host, notBeforeUtc))
                return true;
        }

        return false;
    }

    private static bool EntryMatches(string har, string host, DateTime notBeforeUtc)
    {
        try
        {
            using var doc = JsonDocument.Parse(har);
            if (!doc.RootElement.TryGetProperty("log", out var log) ||
                !log.TryGetProperty("entries", out var entries))
                return false;
            foreach (var entry in entries.EnumerateArray())
            {
                if (!StartedAfter(entry, notBeforeUtc))
                    continue;
                if (!entry.TryGetProperty("request", out var req))
                    continue;
                var method = req.TryGetProperty("method", out var m) ? m.GetString() : null;
                var entryUrl = req.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) || entryUrl is null)
                    continue;
                if (!HostMatches(entryUrl, host))
                    continue;
                var status = 0;
                if (entry.TryGetProperty("response", out var resp) && resp.TryGetProperty("status", out var st))
                    status = st.GetInt32();
                if (status <= 0)
                    continue;
                if (entry.TryGetProperty("_inspector", out var ext) &&
                    ext.TryGetProperty("opaqueReason", out var reason))
                {
                    var text = reason.GetString();
                    if (!string.IsNullOrEmpty(text) && !text.Equals("None", StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                Console.WriteLine("matched GET " + entryUrl);
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// HAR files are flushed after the browser that made the request has exited.
    /// A file timestamp alone would credit Chrome's example.com GET to the next browser.
    /// </summary>
    private static bool StartedAfter(JsonElement entry, DateTime notBeforeUtc)
    {
        if (!entry.TryGetProperty("startedDateTime", out var started))
            return false;
        var text = started.GetString();
        if (string.IsNullOrEmpty(text))
            return false;
        if (!DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var when))
            return false;
        var utc = when.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(when, DateTimeKind.Utc)
            : when.ToUniversalTime();
        return utc >= notBeforeUtc;
    }

    private static bool HostMatches(string entryUrl, string host)
    {
        if (!Uri.TryCreate(entryUrl, UriKind.Absolute, out var uri))
            return entryUrl.Contains(host, StringComparison.OrdinalIgnoreCase);
        if (host.Equals("example.com", StringComparison.OrdinalIgnoreCase))
            return uri.Host.Equals("example.com", StringComparison.OrdinalIgnoreCase)
                   || uri.Host.Equals("www.example.com", StringComparison.OrdinalIgnoreCase);
        if (host.Equals("www.google.com", StringComparison.OrdinalIgnoreCase))
            return uri.Host.EndsWith("google.com", StringComparison.OrdinalIgnoreCase);
        return uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<InspectorProcess> StartAndDecryptAsync(string exe, int port, bool expectAlreadyOn)
    {
        var app = await InspectorProcess.StartAsync(exe, port);
        await TrustRootAsync();
        if (expectAlreadyOn)
            return app;
        return await DecryptAndMaybeRelaunchAsync(app, exe, port);
    }

    private static async Task<InspectorProcess> DecryptAndMaybeRelaunchAsync(InspectorProcess app, string exe, int port)
    {
        await TrustRootAsync();
        var click = await UiClicks.ClickDecryptHttpsAsync(app.Pid, TimeSpan.FromSeconds(30));
        // Product EnableDecryptHttps / background probe can flip decrypt off for ~12s after
        // the first true write. Require a stable on past that window before declaring success.
        if (await WaitDecryptStableAsync(TimeSpan.FromSeconds(20), settle: TimeSpan.FromSeconds(14)))
        {
            Console.WriteLine("click: " + click.How);
            return app;
        }

        if (OperatingSystem.IsWindows())
            throw new InvalidOperationException(click.Error ?? "Decrypt HTTPS click did not stick");

        await app.QuitAsync();
        await app.DisposeAsync();
        PatchDecrypt(true);
        Console.WriteLine("click: FALLBACK (settings seed)");
        return await InspectorProcess.StartAsync(exe, port);
    }

    /// <summary>
    ///     True when <c>decryptHttps</c> becomes true and stays true for <paramref name="settle"/>
    ///     (covers the post-enable MITM probe that can turn decrypt back off).
    /// </summary>
    private static async Task<bool> WaitDecryptStableAsync(TimeSpan timeout, TimeSpan settle)
    {
        var deadline = DateTime.UtcNow + timeout + settle;
        DateTime? firstTrue = null;
        while (DateTime.UtcNow < deadline)
        {
            if (await ReadDecryptAsync())
            {
                firstTrue ??= DateTime.UtcNow;
                if (DateTime.UtcNow - firstTrue.Value >= settle)
                    return true;
            }
            else
            {
                firstTrue = null;
            }

            await Task.Delay(400);
        }

        return false;
    }

    private static async Task<string> TrustRootAsync()
    {
        var pfx = Path.Combine(ProfileDir(), "rootCert.pfx");
        var deadline = DateTime.UtcNow.AddSeconds(40);
        while (!File.Exists(pfx) && DateTime.UtcNow < deadline)
            await Task.Delay(300);
        if (!File.Exists(pfx))
            throw new InvalidOperationException("rootCert.pfx was not written");
        var thumb = ThumbFromPfx();
        if (OperatingSystem.IsWindows())
            TrustWindowsUser(pfx);
        else if (OperatingSystem.IsLinux())
            TrustLinuxNss(pfx);
        else if (OperatingSystem.IsMacOS())
            TrustMac(pfx, thumb);
        return thumb;
    }

    private static void TrustWindowsUser(string pfx)
    {
        using var cert = X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(pfx), ReadOnlySpan<char>.Empty, X509KeyStorageFlags.Exportable);
        var cer = Path.Combine(Path.GetTempPath(), "titanium-smoke-" + cert.Thumbprint + ".cer");
        File.WriteAllBytes(cer, cert.Export(X509ContentType.Cert));
        try
        {
            var certutil = Path.Combine(Environment.SystemDirectory, "certutil.exe");
            // -user can wait on a console confirmation on a hosted image. Answer that prompt.
            // A GUI sheet still times out and is not clicked.
            var user = RunCapture(certutil, "-user -addstore -f Root \"" + cer + "\"", 15000, "y\r\n");
            Console.WriteLine($"certutil -user exit={user.Code} {TrimOneLine(user.Output)}");
            if (!UserRootContains(cert.Thumbprint))
            {
                var machine = RunCapture(certutil, "-addstore -f Root \"" + cer + "\"", 20000);
                Console.WriteLine($"certutil -machine exit={machine.Code} {TrimOneLine(machine.Output)}");
            }

            if (!UserRootContains(cert.Thumbprint) && !MachineRootContains(cert.Thumbprint))
                throw new InvalidOperationException("Root store does not contain " + cert.Thumbprint + " after certutil");
            Console.WriteLine("root in store: " + cert.Thumbprint + " user=" + UserRootContains(cert.Thumbprint));
        }
        finally
        {
            TryDelete(cer);
        }
    }

    private static bool UserRootContains(string thumb) => StoreContains(StoreLocation.CurrentUser, thumb);

    private static bool MachineRootContains(string thumb) => StoreContains(StoreLocation.LocalMachine, thumb);

    private static bool StoreContains(StoreLocation location, string thumb)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, location);
            store.Open(OpenFlags.ReadOnly);
            return store.Certificates.Find(X509FindType.FindByThumbprint, thumb, validOnly: false).Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string TrimOneLine(string text)
    {
        var line = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length <= 180 ? line : line[..180];
    }

    private static void TrustLinuxNss(string pfx)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var db = Path.Combine(home, ".pki", "nssdb");
        Directory.CreateDirectory(db);
        var pem = Path.Combine(Path.GetTempPath(), "titanium-smoke-root.pem");
        File.WriteAllText(pem, ExportPem(pfx));
        Run("certutil", $"-N -d sql:{db} --empty-password", 15000);
        Run("certutil", $"-d sql:{db} -A -t \"C,,\" -n \"Titanium Smoke Root\" -i \"{pem}\"", 15000);
        // Edge reads the system CA bundle. NSS alone is enough for Chrome.
        var crt = Path.Combine(Path.GetTempPath(), "titanium-smoke-root.crt");
        File.Copy(pem, crt, overwrite: true);
        Run("sudo", $"cp \"{crt}\" /usr/local/share/ca-certificates/titanium-smoke-root.crt", 15000);
        Run("sudo", "update-ca-certificates", 20000);
        Console.WriteLine("root in system CA bundle");
    }

    private static void TrustMac(string pfx, string thumb)
    {
        var cer = Path.Combine(Path.GetTempPath(), "titanium-smoke-root.cer");
        File.WriteAllBytes(cer, ExportCer(pfx));
        // Match product trust: login keychain + SSL policy. add-trusted-cert -d from
        // sudo/osascript cannot show the trust dialog ("no user interaction was possible").
        Run("security", $"add-trusted-cert -r trustRoot -p ssl \"{cer}\"", 20000);
        var dump = Capture("security", "dump-trust-settings");
        var compact = dump.Replace(":", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        var hasThumb = compact.Contains(thumb, StringComparison.OrdinalIgnoreCase);
        var hasSslPolicy = dump.Contains("SSL", StringComparison.OrdinalIgnoreCase);
        if (!hasThumb && !hasSslPolicy)
            throw new InvalidOperationException(
                "trust dump has no SSL policy for " + thumb + " after add-trusted-cert -p ssl");
        Console.WriteLine("trusted root " + thumb + " (ssl policy)");
    }

    private static void WriteFreshSettings(int port, bool decrypt)
    {
        Directory.CreateDirectory(ProfileDir());
        var settings = new InspectorSettings
        {
            BindPort = port,
            BindAddress = "127.0.0.1",
            DecryptHttps = decrypt,
            EnableDecryptFailureBypass = false,
            AutoStartCapture = true,
            AutoSystemProxyOnStart = true,
            CheckForUpdatesOnStartup = false,
        };
        File.WriteAllText(Path.Combine(ProfileDir(), "settings.json"), JsonSerializer.Serialize(settings, SettingsJson));
    }

    private static void PatchDecrypt(bool value)
    {
        var path = Path.Combine(ProfileDir(), "settings.json");
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.NameEquals("decryptHttps"))
                    writer.WriteBoolean("decryptHttps", value);
                else
                    prop.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        File.WriteAllBytes(path, stream.ToArray());
    }

    private static async Task<bool> ReadDecryptAsync()
    {
        var path = Path.Combine(ProfileDir(), "settings.json");
        if (!File.Exists(path))
            return false;
        try
        {
            await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = await JsonDocument.ParseAsync(stream);
            return doc.RootElement.TryGetProperty("decryptHttps", out var v) && v.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    private static int ReadBindPort()
    {
        var path = Path.Combine(ProfileDir(), "settings.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var port = doc.RootElement.GetProperty("bindPort").GetInt32();
        if (ForbiddenPorts.Contains(port) || port <= 0)
            throw new InvalidOperationException("saved bindPort is not an ephemeral smoke port: " + port);
        return port;
    }

    private static string ThumbFromPfx()
    {
        var pfx = Path.Combine(ProfileDir(), "rootCert.pfx");
        using var cert = X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(pfx), ReadOnlySpan<char>.Empty, X509KeyStorageFlags.Exportable);
        return cert.Thumbprint;
    }

    private static async Task<bool> WaitProxyAsync(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (ProxyIsOn(port))
                return true;
            await Task.Delay(400);
        }

        return ProxyIsOn(port);
    }

    private static string ProxySnapshot()
    {
        if (!OperatingSystem.IsWindows())
            return "";
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        return "ProxyEnable=" + (key?.GetValue("ProxyEnable")?.ToString() ?? "null")
               + " ProxyServer=" + (key?.GetValue("ProxyServer") as string ?? "");
    }

    private static string LinuxProxyDiag(int port)
    {
        if (!OperatingSystem.IsLinux())
            return "";
        var mode = Capture("gsettings", "get org.gnome.system.proxy mode").Trim();
        var host = Capture("gsettings", "get org.gnome.system.proxy.http host").Trim();
        var conf = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "environment.d", "90-titanium-inspector-proxy.conf");
        var confBit = File.Exists(conf) ? "envdrop=yes" : "envdrop=no";
        return $"gsettings.mode={mode} http.host={host} port={port} {confBit}";
    }

    private static bool ProxyIsOn(int port)
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            var enable = key?.GetValue("ProxyEnable");
            var server = key?.GetValue("ProxyServer") as string ?? "";
            var on = enable is int i && i == 1;
            return on && server.Contains(port.ToString(), StringComparison.Ordinal);
        }

        if (OperatingSystem.IsLinux())
        {
            var conf = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "environment.d", "90-titanium-inspector-proxy.conf");
            if (File.Exists(conf) && File.ReadAllText(conf).Contains(port.ToString(), StringComparison.Ordinal))
                return true;
            var g = Capture("gsettings", "get org.gnome.system.proxy mode");
            if (g.Contains("manual", StringComparison.OrdinalIgnoreCase))
                return true;
            // Under xvfb/dbus-run-session gsettings may be empty while Chromium-family
            // hooks still pin ProxyServer / managed policy at the Inspector port.
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string[] policyDirs =
            [
                Path.Combine(home, ".config", "google-chrome", "policies", "managed"),
                Path.Combine(home, ".config", "chromium", "policies", "managed"),
                Path.Combine(home, ".config", "microsoft-edge", "policies", "managed"),
                Path.Combine(home, ".config", "BraveSoftware", "Brave-Browser", "policies", "managed"),
                Path.Combine(home, ".config", "opera", "policies", "managed"),
            ];
            foreach (var dir in policyDirs)
            {
                var policy = Path.Combine(dir, "titanium-inspector-proxy.json");
                if (File.Exists(policy) &&
                    File.ReadAllText(policy).Contains(port.ToString(), StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        var services = Capture("networksetup", "-listallnetworkservices");
        foreach (var line in services.Split('\n'))
        {
            var service = line.Trim();
            if (service.Length == 0 || service.StartsWith("An asterisk", StringComparison.Ordinal) || service.StartsWith('*'))
                continue;
            var web = Capture("networksetup", "-getwebproxy \"" + service.TrimStart('*').Trim() + "\"");
            if (web.Contains("Enabled: Yes", StringComparison.OrdinalIgnoreCase) &&
                web.Contains(port.ToString(), StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static int FreePort()
    {
        for (var i = 0; i < 20; i++)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            if (!ForbiddenPorts.Contains(port))
                return port;
        }

        throw new InvalidOperationException("no ephemeral port");
    }

    private static async Task<bool> WaitTcpAsync(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, port);
                return true;
            }
            catch
            {
                await Task.Delay(300);
            }
        }

        return false;
    }

    private static string[] DefaultBrowsers()
    {
        if (OperatingSystem.IsMacOS())
            return ["chrome", "edge", "brave", "firefox", "opera", "safari"];
        return ["chrome", "edge", "brave", "firefox", "opera"];
    }

    private static string ProfileDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TitaniumInspector");

    private static string LocalDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TitaniumInspector");

    private static string ReadSharedText(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return "";
        }
    }

    private static void RequireCliVersion(string titanium)
    {
        var result = RunCapture(titanium, "version", 20000);
        Console.WriteLine("titanium version exit=" + result.Code + " " + TrimOneLine(result.Output));
        if (result.Code != 0)
            throw new InvalidOperationException("titanium version failed");
    }

    private static string ExportCliRootPem(string dir)
    {
        var baseDir = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var pfx = Path.Combine(baseDir, "Titanium.Web.Proxy", "rootCert.pfx");
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!File.Exists(pfx) && DateTime.UtcNow < deadline)
            Thread.Sleep(200);
        if (!File.Exists(pfx))
            throw new InvalidOperationException("CLI rootCert.pfx was not written at " + pfx);
        var pem = Path.Combine(dir, "cli-root.pem");
        File.WriteAllText(pem, ExportPem(pfx));
        return pem;
    }

    private static string ExportPem(string pfx)
    {
        using var cert = X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(pfx), ReadOnlySpan<char>.Empty, X509KeyStorageFlags.Exportable);
        var b64 = Convert.ToBase64String(cert.Export(X509ContentType.Cert));
        var sb = new StringBuilder();
        sb.Append("-----BEGIN CERTIFICATE-----\n");
        for (var i = 0; i < b64.Length; i += 64)
            sb.Append(b64.AsSpan(i, Math.Min(64, b64.Length - i))).Append('\n');
        sb.Append("-----END CERTIFICATE-----\n");
        return sb.ToString();
    }

    private static byte[] ExportCer(string pfx)
    {
        using var cert = X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(pfx), ReadOnlySpan<char>.Empty, X509KeyStorageFlags.Exportable);
        return cert.Export(X509ContentType.Cert);
    }

    private static string? ReadThumbFile(string? dir)
    {
        if (string.IsNullOrEmpty(dir))
            return null;
        var path = Path.Combine(dir, "root-thumb.txt");
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    private static void DumpDiagnostics(string? outDir)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TitaniumInspector",
            "session-cache");
        if (Directory.Exists(root))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.har", SearchOption.AllDirectories))
                Console.WriteLine("har " + file);
        }
        else
        {
            Console.WriteLine("har directory missing " + root);
        }

        var ux = Path.Combine(ProfileDir(), "logs", "ux-trace.log");
        if (!File.Exists(ux))
            return;
        var lines = File.ReadAllLines(ux);
        var tail = string.Join(Environment.NewLine, lines.TakeLast(40));
        Console.WriteLine("--- ux-trace ---");
        Console.WriteLine(tail);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "ux-trace-tail.log"), tail);
        }
    }

    private static async Task QuitPidAsync(int pid)
    {
        try
        {
            var proc = Process.GetProcessById(pid);
            if (OperatingSystem.IsWindows())
            {
                proc.CloseMainWindow();
                if (!proc.WaitForExit(5000))
                    proc.Kill(entireProcessTree: true);
            }
            else
            {
                proc.Kill(entireProcessTree: true);
            }
        }
        catch { /* already exited */ }
        await Task.Delay(500);
    }

    private static (int Code, string Output) RunCapture(string file, string args, int timeoutMs, string? stdin = null)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = stdin is not null,
        });
        if (p is null)
            return (-1, "did not start");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (stdin is not null)
        {
            p.StandardInput.Write(stdin);
            p.StandardInput.Close();
        }

        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* dialog */ }
            return (-1, "timed out");
        }

        return (p.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }

    private static void Run(string file, string args, int timeoutMs) => RunCapture(file, args, timeoutMs);

    private static string Capture(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null || !p.WaitForExit(8000))
                return "";
            return p.StandardOutput.ReadToEnd();
        }
        catch
        {
            return "";
        }
    }

    private static void TryDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("wipe " + path + ": " + ex.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* temp */ }
    }

    private static string Require(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException(name + " is required") : value;

    private static int Fail(int code, string message)
    {
        Console.WriteLine(message);
        return code;
    }
}

internal sealed class InspectorProcess : IAsyncDisposable
{
    private readonly Process _process;
    public int Pid => _process.Id;
    public bool HasExited => _process.HasExited;
    public DateTime StartedUtc { get; }

    private InspectorProcess(Process process)
    {
        _process = process;
        StartedUtc = DateTime.UtcNow;
    }

    public static Task<InspectorProcess> StartAsync(string exe, int port)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
        };
        psi.Environment["TITANIUM_SKIP_ROOT_STORE_UI"] = "1";
        psi.Environment["TITANIUM_UPDATE_FEED"] = "";
        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Inspector did not start: " + exe);
        return Task.FromResult(new InspectorProcess(proc));
    }

    public async Task<bool> CaptureIsListeningAsync()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TitaniumInspector", "settings.json");
        if (!File.Exists(path))
            return false;
        int port;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            port = doc.RootElement.GetProperty("bindPort").GetInt32();
        }
        catch
        {
            return false;
        }

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, port);
                return true;
            }
            catch
            {
                await Task.Delay(300);
            }
        }

        return false;
    }

    public async Task QuitAsync()
    {
        try
        {
            if (_process.HasExited)
                return;
            if (OperatingSystem.IsWindows())
            {
                _process.CloseMainWindow();
                if (!_process.WaitForExit(4000))
                {
                    using var kill = Process.Start(new ProcessStartInfo
                    {
                        FileName = "taskkill",
                        Arguments = "/F /T /PID " + _process.Id,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });
                    kill?.WaitForExit(8000);
                }
            }
            else
            {
                _process.Kill(entireProcessTree: false);
            }

            _process.WaitForExit(5000);
        }
        catch { /* already gone */ }
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await QuitAsync();
        _process.Dispose();
    }
}

internal sealed class BrowserLaunch : IDisposable
{
    private readonly Process? _process;
    private readonly bool _safari;
    private readonly string? _dir;
    private readonly string? _killName;

    public BrowserLaunch(Process? process, bool safari, string? dir = null, string? killName = null)
    {
        _process = process;
        _safari = safari;
        _dir = dir;
        _killName = killName;
    }

    public void Kill()
    {
        try
        {
            if (_safari)
                SystemProxyBrowserCapture.KillSafariProcesses();
            else if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
            if (!string.IsNullOrEmpty(_killName))
            {
                foreach (var proc in new[] { _killName }.Concat(ExtraKillNames(_killName)))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "killall",
                        ArgumentList = { proc },
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    })?.WaitForExit(3000);
                }
            }
        }
        catch { /* already gone */ }
        if (_dir is not null)
        {
            try { Directory.Delete(_dir, true); } catch { /* in use */ }
        }
    }

    public void Dispose() => Kill();

    private static IEnumerable<string> ExtraKillNames(string killName) => killName switch
    {
        "msedge" => ["microsoft-edge", "microsoft-edge-stable"],
        "chrome" => ["google-chrome", "google-chrome-stable"],
        "brave" => ["brave-browser"],
        "opera" => ["opera-stable"],
        _ => [],
    };
}

/// <summary>
/// Backs up the real Inspector profile and WinINET proxy, parks them for the run,
/// and puts them back. Removes only root thumbprints this run added.
/// </summary>
internal sealed class WindowsProfileGuard : IDisposable
{
    private readonly string _backup;
    private readonly bool _hadProfile;
    private readonly bool _hadLocal;
    private readonly int _proxyEnable;
    private readonly string? _proxyServer;
    private readonly string? _proxyOverride;
    private readonly HashSet<string> _rootsBefore;
    private readonly string _profile;
    private readonly string _local;

    private WindowsProfileGuard(
        string backup, bool hadProfile, bool hadLocal,
        int proxyEnable, string? proxyServer, string? proxyOverride,
        HashSet<string> rootsBefore, string profile, string local)
    {
        _backup = backup;
        _hadProfile = hadProfile;
        _hadLocal = hadLocal;
        _proxyEnable = proxyEnable;
        _proxyServer = proxyServer;
        _proxyOverride = proxyOverride;
        _rootsBefore = rootsBefore;
        _profile = profile;
        _local = local;
    }

    public static WindowsProfileGuard Capture()
    {
        var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TitaniumInspector");
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TitaniumInspector");
        var backup = Path.Combine(Path.GetTempPath(), "titanium-smoke-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        var hadProfile = Directory.Exists(profile);
        var hadLocal = Directory.Exists(local);
        if (hadProfile)
            Robocopy(profile, Path.Combine(backup, "roaming"));
        if (hadLocal)
            Robocopy(local, Path.Combine(backup, "local"));
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        var enable = key?.GetValue("ProxyEnable") is int i ? i : 0;
        var server = key?.GetValue("ProxyServer") as string;
        var over = key?.GetValue("ProxyOverride") as string;
        return new WindowsProfileGuard(backup, hadProfile, hadLocal, enable, server, over, CurrentRootThumbs(), profile, local);
    }

    public void ParkProfile()
    {
        TryDelete(_profile);
        TryDelete(_local);
    }

    public void Restore()
    {
        TryDelete(_profile);
        TryDelete(_local);
        if (_hadProfile)
            Robocopy(Path.Combine(_backup, "roaming"), _profile);
        if (_hadLocal)
            Robocopy(Path.Combine(_backup, "local"), _local);
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", writable: true);
        key?.SetValue("ProxyEnable", _proxyEnable, Microsoft.Win32.RegistryValueKind.DWord);
        if (_proxyServer is null)
            key?.DeleteValue("ProxyServer", throwOnMissingValue: false);
        else
            key?.SetValue("ProxyServer", _proxyServer, Microsoft.Win32.RegistryValueKind.String);
        if (_proxyOverride is null)
            key?.DeleteValue("ProxyOverride", throwOnMissingValue: false);
        else
            key?.SetValue("ProxyOverride", _proxyOverride, Microsoft.Win32.RegistryValueKind.String);
        NotifyProxy();
        foreach (var thumb in CurrentRootThumbs())
        {
            if (!_rootsBefore.Contains(thumb))
                RemoveUserRoot(thumb);
        }
    }

    public bool IsClean()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        var enable = key?.GetValue("ProxyEnable") is int i ? i : 0;
        var server = key?.GetValue("ProxyServer") as string;
        if (enable != _proxyEnable || !string.Equals(server, _proxyServer, StringComparison.Ordinal))
            return false;
        var live = Path.Combine(_profile, "settings.json");
        var backed = Path.Combine(_backup, "roaming", "settings.json");
        if (_hadProfile)
        {
            if (!File.Exists(live) || !File.Exists(backed))
                return false;
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(live))) ==
                   Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(backed)));
        }

        return !Directory.Exists(_profile);
    }

    public static void RemoveUserRoot(string thumb)
    {
        try
        {
            using var hive = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\SystemCertificates\Root\Certificates", writable: true);
            hive?.DeleteSubKeyTree(thumb, throwOnMissingSubKey: false);
        }
        catch { /* registry */ }
        var certutil = Path.Combine(Environment.SystemDirectory, "certutil.exe");
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = certutil,
            Arguments = "-user -delstore Root " + thumb,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        p?.WaitForExit(8000);
    }

    public void Dispose() => Restore();

    private static HashSet<string> CurrentRootThumbs()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        foreach (var cert in store.Certificates)
            set.Add(cert.Thumbprint);
        return set;
    }

    private static void NotifyProxy()
    {
        InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
    }

    [System.Runtime.InteropServices.DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    private static void Robocopy(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = "robocopy",
            Arguments = $"\"{src}\" \"{dest}\" /MIR /NFL /NDL /NJH /NJS /R:1 /W:1",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        p?.WaitForExit(120000);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch { /* parked */ }
    }
}

internal readonly record struct Options(
    string? Exe,
    string? PrevExe,
    string? Titanium,
    string? Out,
    string? Thumb,
    string? ExpectThumb,
    string[]? Browsers)
{
    public static Options Parse(string[] args)
    {
        string? exe = null, prev = null, titanium = null, output = null, thumb = null, expect = null;
        string[]? browsers = null;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new InvalidOperationException(a + " needs a value");
            switch (a)
            {
                case "--exe": exe = Next(); break;
                case "--prev-exe": prev = Next(); break;
                case "--titanium": titanium = Next(); break;
                case "--out": output = Next(); break;
                case "--thumb": thumb = Next(); break;
                case "--expect-thumb": expect = Next(); break;
                case "--browsers": browsers = Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
                case "--mode": Next(); break;
                default: throw new InvalidOperationException("unknown option " + a);
            }
        }

        return new Options(exe, prev, titanium, output, thumb, expect, browsers);
    }
}

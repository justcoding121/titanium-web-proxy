using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Titanium.Cli.QaProbe;

public static class ScenarioRunner
{
    /// <summary>Direct (non-proxy) HttpClient — avoids WinINET leftovers hijacking loopback HTTPS.</summary>
    private static HttpClient CreateDirectHttp(TimeSpan? timeout = null)
    {
        var handler = new HttpClientHandler { UseProxy = false };
        return new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(20) };
    }

    private static HttpClient CreateProxyHttp(int proxyPort, bool ignoreServerCert = false, TimeSpan? timeout = null)
    {
        var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}"),
            UseProxy = true,
        };
        if (ignoreServerCert)
            handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null;
        return new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(25) };
    }

    public static void TryDisableSystemProxy()
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", writable: true);
            key?.SetValue("ProxyEnable", 0);
        }
        catch
        {
            // best-effort
        }
    }

    public static async Task<int> RunStatusAsync(ProbeLog log)
    {
        try
        {
            using var spawn = new CliSpawn();
            log.Info($"titanium.dll: {spawn.CliDllPath}");
            log.Info($"OS: {RuntimeInformation.OSDescription}");
            log.Info($"Elevated: {Elevation.IsElevated()}");
            if (File.Exists(log.LastRunJsonPath))
            {
                log.Info($"last-run.json: {log.LastRunJsonPath}");
                try
                {
                    using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(log.LastRunJsonPath));
                    if (doc.RootElement.TryGetProperty("Command", out var cmd))
                        log.Info($"last command: {cmd.GetString()}");
                    if (doc.RootElement.TryGetProperty("ExitCode", out var code))
                        log.Info($"last exit: {code.GetInt32()}");
                }
                catch (Exception ex)
                {
                    log.Warn("Could not parse last-run.json: " + ex.Message);
                }
            }
            else
            {
                log.Info("No last-run.json yet.");
            }

            log.Step("status", true, "ok");
            return 0;
        }
        catch (Exception ex)
        {
            log.Step("status", false, ex.Message);
            return 1;
        }
    }

    public static async Task<int> RunHelpMatrixAsync(ProbeLog log)
    {
        using var spawn = new CliSpawn();
        var fails = 0;
        fails += await HelpStep(log, spawn, "help-root", ["help"], text =>
            ContainsAll(text, "run", "test", "service", "http3-deps"));
        fails += await HelpStep(log, spawn, "help-run", ["run", "--help"], text =>
            text.Contains("-c", StringComparison.Ordinal) &&
            text.Contains("--service", StringComparison.Ordinal) &&
            text.Contains("--name", StringComparison.Ordinal) &&
            !text.Contains("Missing required -c", StringComparison.OrdinalIgnoreCase));
        fails += await HelpStep(log, spawn, "help-test", ["test", "--help"], _ => true);
        fails += await HelpStep(log, spawn, "help-version", ["version", "--help"], _ => true);
        fails += await HelpStep(log, spawn, "help-update", ["update", "--help"], text =>
            !text.Contains("Checking for updates", StringComparison.OrdinalIgnoreCase));
        fails += await HelpStep(log, spawn, "help-http3", ["http3-deps", "--help"], _ => true);
        fails += await HelpStep(log, spawn, "help-service", ["service", "--help"], text =>
            ContainsAll(text, "install", "uninstall", "start", "stop", "restart", "status"));
        fails += await HelpStep(log, spawn, "help-service-install", ["service", "install", "--help"], text =>
            ContainsAll(text, "-c", "--name", "--user", "--no-start"));
        fails += await HelpStep(log, spawn, "help-service-start", ["service", "start", "--help"], _ => true);

        // Also check no-args lists key commands (exit 1 is OK)
        {
            var (code, stdout, stderr) = await spawn.RunOnceAsync([]);
            var text = stdout + stderr;
            var ok = ContainsAll(text, "run", "test", "service", "http3-deps");
            log.Step("help-root-noargs", ok, $"exit={code}");
            if (!ok) fails++;
        }

        return fails == 0 ? 0 : 1;
    }

    public static async Task<int> RunMetaAsync(ProbeLog log)
    {
        using var spawn = new CliSpawn();
        var fails = 0;

        {
            var (code, stdout, _) = await spawn.RunOnceAsync(["version"]);
            var ok = code == 0 && stdout.Contains("7.", StringComparison.Ordinal);
            log.Step("version", ok, $"exit={code} out={Trim(stdout)}");
            if (!ok) fails++;
        }

        {
            try
            {
                var (code, stdout, stderr) = await spawn.RunOnceAsync(
                    ["version", "--check"],
                    timeout: TimeSpan.FromSeconds(45));
                var ok = code is 0 or 1 or 2;
                log.Step("version-check", ok, $"exit={code} (soft offline) {Trim(stdout + stderr)}",
                    skipped: false);
                if (!ok) fails++;
            }
            catch (Exception ex)
            {
                log.Step("version-check", true, "soft skip: " + ex.Message, skipped: true);
            }
        }

        {
            var (code, stdout, stderr) = await spawn.RunOnceAsync(["http3-deps", "status"]);
            var ok = code == 0;
            log.Step("http3-deps-status", ok, $"exit={code} {Trim(stdout + stderr)}");
            if (!ok) fails++;
        }

        return fails == 0 ? 0 : 1;
    }

    public static async Task<int> RunTestDialectsAsync(ProbeLog log)
    {
        using var spawn = new CliSpawn();
        using var origin = new EchoOrigin();
        var temp = MakeTemp();
        var fails = 0;
        try
        {
            var listen = CliSpawn.GetFreePort();
            fails += await TestOk(log, spawn, "test-yaml", ConfigWriter.WriteForwardHost(temp, listen, origin.Port));
            fails += await TestOk(log, spawn, "test-json", ConfigWriter.WriteRoutes(temp, CliSpawn.GetFreePort(), origin.Port));
            fails += await TestOk(log, spawn, "test-twp", ConfigWriter.WriteSiteFileListenForward(temp, CliSpawn.GetFreePort(), origin.Port));
            fails += await TestOk(log, spawn, "test-conf", ConfigWriter.WriteHttpServerConf(temp, CliSpawn.GetFreePort(), origin.Port));

            var invalid = ConfigWriter.WriteInvalid(temp);
            var (code, _, stderr) = await spawn.RunOnceAsync(["test", "-c", invalid]);
            var ok = code != 0;
            log.Step("test-invalid", ok, $"exit={code} {Trim(stderr)}");
            if (!ok) fails++;
        }
        finally
        {
            TryDelete(temp);
        }

        return fails == 0 ? 0 : 1;
    }

    public static async Task<int> RunLiveCoreAsync(ProbeLog log)
    {
        var fails = 0;
        fails += await RunForwardAsync(log);
        fails += await RunConfAsync(log);
        fails += await RunStaticAsync(log);
        return fails == 0 ? 0 : 1;
    }

    public static async Task<int> RunLiveRemainingAsync(ProbeLog log)
    {
        var fails = 0;
        fails += await RunSiteFileAsync(log);
        fails += await RunRoutesAsync(log);
        fails += await RunTlsAsync(log);
        fails += await RunMitmAsync(log);
        fails += await RunHttp2OffAsync(log);
        fails += await RunLoggingAsync(log);
        fails += await RunPlusAsync(log);
        return fails == 0 ? 0 : 1;
    }

    /// <summary>Subset for <c>core</c>: mitm + logging (forward/conf/static already run).</summary>
    public static async Task<int> RunCoreTrafficExtrasAsync(ProbeLog log)
    {
        var fails = 0;
        fails += await RunMitmAsync(log);
        fails += await RunLoggingAsync(log);
        return fails == 0 ? 0 : 1;
    }

    public static async Task<int> RunServiceSectionAsync(ProbeLog log, bool elevatedRequested)
    {
        using var spawn = new CliSpawn();
        var temp = MakeTemp();
        var fails = 0;
        var elevated = elevatedRequested || Elevation.IsElevated();

        try
        {
            {
                var (code, stdout, stderr) = await spawn.RunOnceAsync(
                    ["service", "status", "--name", Elevation.QaServiceName],
                    timeout: TimeSpan.FromSeconds(30));
                var text = stdout + stderr;
                var ok = code == 1 || text.Contains("not installed", StringComparison.OrdinalIgnoreCase);
                // If leftover from prior elevated run is installed, still pass as long as no hang.
                if (code == 0 && text.Contains(Elevation.QaServiceName, StringComparison.OrdinalIgnoreCase))
                    ok = true;
                log.Step("service-status-missing", ok, $"exit={code} {Trim(text)}");
                if (!ok) fails++;
            }

            if (!Elevation.IsElevated())
            {
                using var origin = new EchoOrigin();
                var cfg = ConfigWriter.WriteForwardHost(temp, CliSpawn.GetFreePort(), origin.Port);
                var (code, stdout, stderr) = await spawn.RunOnceAsync(
                    ["service", "install", "-c", cfg, "--name", Elevation.QaServiceName, "--no-start"],
                    timeout: TimeSpan.FromSeconds(45));
                var text = stdout + stderr;
                var ok = code != 0 && (
                    text.Contains("Administrator", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("sudo", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("Root privileges", StringComparison.OrdinalIgnoreCase));
                log.Step("service-install-unelevated", ok, $"exit={code} {Trim(text)}");
                if (!ok) fails++;
            }
            else
            {
                log.Step("service-install-unelevated", true, "skipped (already elevated)", skipped: true);
            }

            if (elevated)
            {
                fails += await RunServiceLifecycleAsync(log, spawn, temp);
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    fails += await RunServiceUserAsync(log, spawn, temp);
                else
                    log.Step("service-user", true, "skipped on Windows", skipped: true);
            }
            else if (elevatedRequested)
            {
                log.Step("service-lifecycle", false, "--elevated requested but process is not admin/root");
                fails++;
            }
            else
            {
                log.Step("service-lifecycle", true, "skipped (pass --elevated or run as admin)", skipped: true);
                log.Step("service-user", true, "skipped", skipped: true);
            }
        }
        finally
        {
            TryDelete(temp);
        }

        return fails == 0 ? 0 : 1;
    }

    private static async Task<int> RunServiceLifecycleAsync(ProbeLog log, CliSpawn spawn, string temp)
    {
        using var origin = new EchoOrigin();
        var listen = CliSpawn.GetFreePort();
        var cfg = ConfigWriter.WriteForwardHost(temp, listen, origin.Port);
        var name = Elevation.QaServiceName;
        var fails = 0;

        try
        {
            // Best-effort cleanup of leftovers
            _ = await spawn.RunOnceAsync(["service", "uninstall", "--name", name], timeout: TimeSpan.FromSeconds(60));

            var (installCode, installOut, installErr) = await spawn.RunOnceAsync(
                ["service", "install", "-c", cfg, "--name", name, "--no-start"],
                timeout: TimeSpan.FromSeconds(90));
            var installOk = installCode == 0;
            log.Step("service-lifecycle-install", installOk, $"exit={installCode} {Trim(installOut + installErr)}");
            if (!installOk) return 1;

            var (stCode, stOut, stErr) = await spawn.RunOnceAsync(
                ["service", "status", "--name", name], timeout: TimeSpan.FromSeconds(30));
            var stOk = stCode == 0 && (stOut + stErr).Contains("stopped", StringComparison.OrdinalIgnoreCase);
            log.Step("service-lifecycle-status", stOk, $"exit={stCode} {Trim(stOut + stErr)}");
            if (!stOk) fails++;

            var (startCode, startOut, startErr) = await spawn.RunOnceAsync(
                ["service", "start", "--name", name], timeout: TimeSpan.FromSeconds(90));
            var startOk = startCode == 0;
            log.Step("service-lifecycle-start", startOk, $"exit={startCode} {Trim(startOut + startErr)}");
            if (!startOk) fails++;
            else
            {
                // Wait for listener
                var httpOk = false;
                string detail = "";
                for (var i = 0; i < 40; i++)
                {
                    try
                    {
                        using var http = CreateDirectHttp(TimeSpan.FromSeconds(3));
                        var resp = await http.GetAsync($"http://127.0.0.1:{listen}/svc");
                        detail = $"status={(int)resp.StatusCode}";
                        if (resp.StatusCode == HttpStatusCode.OK)
                        {
                            httpOk = true;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        detail = ex.Message;
                    }

                    await Task.Delay(500);
                }

                log.Step("service-lifecycle-http", httpOk, detail);
                if (!httpOk) fails++;
            }

            var (stopCode, stopOut, stopErr) = await spawn.RunOnceAsync(
                ["service", "stop", "--name", name], timeout: TimeSpan.FromSeconds(90));
            var stopOk = stopCode == 0;
            log.Step("service-lifecycle-stop", stopOk, $"exit={stopCode} {Trim(stopOut + stopErr)}");
            if (!stopOk) fails++;
        }
        finally
        {
            try
            {
                _ = await spawn.RunOnceAsync(["service", "stop", "--name", name], timeout: TimeSpan.FromSeconds(60));
            }
            catch { /* ignore */ }

            var (unCode, unOut, unErr) = await spawn.RunOnceAsync(
                ["service", "uninstall", "--name", name], timeout: TimeSpan.FromSeconds(90));
            var unOk = unCode == 0 || (unOut + unErr).Contains("not installed", StringComparison.OrdinalIgnoreCase);
            log.Step("service-lifecycle-uninstall", unOk, $"exit={unCode} {Trim(unOut + unErr)}");
            if (!unOk) fails++;
        }

        log.Step("service-lifecycle", fails == 0, fails == 0 ? "ok" : $"{fails} substep(s) failed");
        return fails == 0 ? 0 : 1;
    }

    private static async Task<int> RunServiceUserAsync(ProbeLog log, CliSpawn spawn, string temp)
    {
        using var origin = new EchoOrigin();
        var listen = CliSpawn.GetFreePort();
        var cfg = ConfigWriter.WriteForwardHost(temp, listen, origin.Port);
        var name = Elevation.QaServiceName;
        var fails = 0;

        try
        {
            _ = await spawn.RunOnceAsync(
                ["service", "uninstall", "--name", name, "--user"],
                timeout: TimeSpan.FromSeconds(60));

            var (installCode, installOut, installErr) = await spawn.RunOnceAsync(
                ["service", "install", "-c", cfg, "--name", name, "--user", "--no-start"],
                timeout: TimeSpan.FromSeconds(90));
            var installOk = installCode == 0;
            log.Step("service-user-install", installOk, $"exit={installCode} {Trim(installOut + installErr)}");
            if (!installOk) return 1;

            var (stCode, stOut, stErr) = await spawn.RunOnceAsync(
                ["service", "status", "--name", name, "--user"],
                timeout: TimeSpan.FromSeconds(30));
            var stOk = stCode == 0;
            log.Step("service-user-status", stOk, $"exit={stCode} {Trim(stOut + stErr)}");
            if (!stOk) fails++;
        }
        finally
        {
            var (unCode, unOut, unErr) = await spawn.RunOnceAsync(
                ["service", "uninstall", "--name", name, "--user"],
                timeout: TimeSpan.FromSeconds(90));
            var unOk = unCode == 0 || (unOut + unErr).Contains("not installed", StringComparison.OrdinalIgnoreCase);
            log.Step("service-user-uninstall", unOk, $"exit={unCode} {Trim(unOut + unErr)}");
            if (!unOk) fails++;
        }

        log.Step("service-user", fails == 0, fails == 0 ? "ok" : "failed");
        return fails == 0 ? 0 : 1;
    }

    private static async Task<int> RunForwardAsync(ProbeLog log)
    {
        using var origin = new EchoOrigin();
        var temp = MakeTemp();
        try
        {
            var listen = CliSpawn.GetFreePort();
            var cfg = ConfigWriter.WriteForwardHost(temp, listen, origin.Port);
            using var spawn = new CliSpawn();
            await spawn.StartRunAsync(cfg);
            using var http = CreateDirectHttp();
            var resp = await http.GetAsync($"http://127.0.0.1:{listen}/hello");
            var body = await resp.Content.ReadAsStringAsync();
            var ok = resp.StatusCode == HttpStatusCode.OK && body.Contains("echo:", StringComparison.Ordinal);
            log.Step("run-forward", ok, $"status={(int)resp.StatusCode} body={Trim(body)}");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            log.Step("run-forward", false, ex.Message);
            return 1;
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static async Task<int> RunConfAsync(ProbeLog log)
    {
        using var origin = new EchoOrigin();
        var temp = MakeTemp();
        try
        {
            var listen = CliSpawn.GetFreePort();
            var cfg = ConfigWriter.WriteHttpServerConf(temp, listen, origin.Port);
            using var spawn = new CliSpawn();
            await spawn.StartRunAsync(cfg);
            using var http = CreateDirectHttp();
            var resp = await http.GetAsync($"http://127.0.0.1:{listen}/conf");
            var body = await resp.Content.ReadAsStringAsync();
            var ok = resp.StatusCode == HttpStatusCode.OK && body.Contains("echo:", StringComparison.Ordinal);
            log.Step("run-conf", ok, $"status={(int)resp.StatusCode} body={Trim(body)}");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            log.Step("run-conf", false, ex.Message);
            return 1;
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static async Task<int> RunSiteFileAsync(ProbeLog log)
    {
        using var origin = new EchoOrigin();
        var temp = MakeTemp();
        try
        {
            var listen = CliSpawn.GetFreePort();
            var cfg = ConfigWriter.WriteSiteFileListenForward(temp, listen, origin.Port);
            using var spawn = new CliSpawn();
            await spawn.StartRunAsync(cfg);
            using var http = CreateDirectHttp();
            var resp = await http.GetAsync($"http://127.0.0.1:{listen}/site");
            var body = await resp.Content.ReadAsStringAsync();
            var ok = resp.StatusCode == HttpStatusCode.OK && body.Contains("echo:", StringComparison.Ordinal);
            log.Step("run-sitefile", ok, $"status={(int)resp.StatusCode} body={Trim(body)}");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            log.Step("run-sitefile", false, ex.Message);
            return 1;
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static async Task<int> RunRoutesAsync(ProbeLog log)
    {
        using var origin = new EchoOrigin();
        var temp = MakeTemp();
        try
        {
            var listen = CliSpawn.GetFreePort();
            var cfg = ConfigWriter.WriteRoutes(temp, listen, origin.Port);
            using var spawn = new CliSpawn();
            await spawn.StartRunAsync(cfg);
            using var http = CreateProxyHttp(listen);
            var resp = await http.GetAsync($"http://127.0.0.1:{origin.Port}/routed");
            var body = await resp.Content.ReadAsStringAsync();
            var ok = resp.StatusCode == HttpStatusCode.OK && body.Contains("echo:", StringComparison.Ordinal);
            log.Step("run-routes", ok, $"status={(int)resp.StatusCode} body={Trim(body)}");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            log.Step("run-routes", false, ex.Message);
            return 1;
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static async Task<int> RunStaticAsync(ProbeLog log)
    {
        var temp = MakeTemp();
        try
        {
            var www = Path.Combine(temp, "www");
            Directory.CreateDirectory(www);
            await File.WriteAllTextAsync(Path.Combine(www, "index.html"), "<html>ok</html>");
            var listen = CliSpawn.GetFreePort();
            var cfg = ConfigWriter.WriteStatic(temp, listen, www);
            using var spawn = new CliSpawn();
            await spawn.StartRunAsync(cfg);
            using var http = CreateProxyHttp(listen);
            var resp = await http.GetAsync($"http://127.0.0.1:{listen}/");
            var body = await resp.Content.ReadAsStringAsync();
            var hasEtag = resp.Headers.ETag is not null ||
                          resp.Headers.Contains("ETag") ||
                          resp.Content.Headers.Contains("ETag");
            var ok = resp.StatusCode == HttpStatusCode.OK && body.Contains("ok", StringComparison.Ordinal) && hasEtag;
            log.Step("run-static", ok, $"status={(int)resp.StatusCode} etag={hasEtag}");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            log.Step("run-static", false, ex.Message);
            return 1;
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static async Task<int> RunTlsAsync(ProbeLog log)
    {
        using var origin = new EchoOrigin();
        var temp = MakeTemp();
        try
        {
            var (certPath, keyPath) = ConfigWriter.WriteSelfSignedPem(temp);
            var listen = CliSpawn.GetFreePort();
            var cfg = ConfigWriter.WriteTls(temp, listen, origin.Port, certPath, keyPath);
            using var spawn = new CliSpawn();
            await spawn.StartRunAsync(cfg);
            if (!spawn.StdOut.Contains("Loaded leaf", StringComparison.OrdinalIgnoreCase) &&
                !spawn.StdOut.Contains("Certificate path configured", StringComparison.OrdinalIgnoreCase))
            {
                log.Step("run-tls", false, "leaf cert not loaded: " + Trim(spawn.StdOut + spawn.StdErr));
                return 1;
            }

            using var handler = new HttpClientHandler
            {
                UseProxy = false,
                ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            var resp = await http.GetAsync($"https://localhost:{listen}/tls");
            var body = await resp.Content.ReadAsStringAsync();
            var ok = resp.StatusCode == HttpStatusCode.OK && body.Contains("echo:", StringComparison.Ordinal);
            log.Step("run-tls", ok, $"status={(int)resp.StatusCode} body={Trim(body)}");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            log.Step("run-tls", false, ex.GetBaseException().Message);
            return 1;
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static async Task<int> RunMitmAsync(ProbeLog log)
    {
        using var httpsOrigin = new HttpsEchoOrigin();
        var temp = MakeTemp();
        try
        {
            var listen = CliSpawn.GetFreePort();
            var cfg = ConfigWriter.WriteExplicitMitm(temp, listen);
            using var spawn = new CliSpawn();
            await spawn.StartRunAsync(cfg);
            using var http = CreateProxyHttp(listen, ignoreServerCert: true);
            var resp = await http.GetAsync($"https://127.0.0.1:{httpsOrigin.Port}/mitm");
            var body = await resp.Content.ReadAsStringAsync();
            var ok = resp.StatusCode == HttpStatusCode.OK && body.Contains("echo:", StringComparison.OrdinalIgnoreCase);
            log.Step("run-mitm", ok, $"status={(int)resp.StatusCode} body={Trim(body)}");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            log.Step("run-mitm", false, ex.ToString());
            return 1;
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static async Task<int> RunHttp2OffAsync(ProbeLog log)
    {
        using var origin = new EchoOrigin();
        var temp = MakeTemp();
        try
        {
            var listen = CliSpawn.GetFreePort();
            var cfg = ConfigWriter.WriteListenerFlags(temp, listen, origin.Port, enableHttp2: false, enableHttp3: false);
            using var spawn = new CliSpawn();
            await spawn.StartRunAsync(cfg);
            using var http = CreateDirectHttp();
            var resp = await http.GetAsync($"http://127.0.0.1:{listen}/flags");
            var body = await resp.Content.ReadAsStringAsync();
            var ok = resp.StatusCode == HttpStatusCode.OK && body.Contains("echo:", StringComparison.Ordinal);
            log.Step("run-http2-off", ok, $"status={(int)resp.StatusCode} body={Trim(body)}");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            log.Step("run-http2-off", false, ex.Message);
            return 1;
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static async Task<int> RunLoggingAsync(ProbeLog log)
    {
        using var origin = new EchoOrigin();
        var temp = MakeTemp();
        try
        {
            var listen = CliSpawn.GetFreePort();
            var logFile = Path.Combine(temp, "cli-qa.log");
            var cfg = ConfigWriter.WriteLogging(temp, listen, origin.Port, logFile);
            using var spawn = new CliSpawn();
            await spawn.StartRunAsync(cfg);
            // Touch traffic so file logger flushes useful content
            using var http = CreateDirectHttp(TimeSpan.FromSeconds(10));
            _ = await http.GetAsync($"http://127.0.0.1:{listen}/log");

            var found = false;
            string detail = "log missing";
            for (var i = 0; i < 30; i++)
            {
                if (File.Exists(logFile))
                {
                    var text = await ReadSharedAsync(logFile);
                    if (text.Contains("running", StringComparison.OrdinalIgnoreCase) ||
                        text.Contains("Starting", StringComparison.OrdinalIgnoreCase) ||
                        text.Contains("Titanium", StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        detail = "file contains running/start marker";
                        break;
                    }

                    detail = "file present but no marker yet: " + Trim(text);
                }

                await Task.Delay(200);
            }

            // Require a real log marker — empty/partial file is a fail.
            if (!found)
            {
                log.Step("run-logging", false, detail);
                return 1;
            }

            log.Step("run-logging", true, detail);
            return 0;
        }
        catch (Exception ex)
        {
            log.Step("run-logging", false, ex.Message);
            return 1;
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static async Task<int> RunPlusAsync(ProbeLog log)
    {
        var temp = MakeTemp();
        try
        {
            using var spawn = new CliSpawn();
            if (!spawn.TryEnsurePlusDll())
            {
                log.Step("run-plus", true, "Titanium.Plus.dll not built — skip", skipped: true);
                return 0;
            }

            using var origin = new EchoOrigin();
            var listen = CliSpawn.GetFreePort();
            var control = CliSpawn.GetFreePort();
            const string secret = "cli-qa-plus-secret";
            var cfg = ConfigWriter.WritePlus(temp, listen, origin.Port, control, secret);
            await spawn.StartRunAsync(cfg, verbose: false, env: new Dictionary<string, string?>
            {
                ["TITANIUM_PLUS_ALLOW_DEV_SECRET"] = "1",
            });

            using var plain = CreateDirectHttp(TimeSpan.FromSeconds(15));
            using (var unauth = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{control}/v1/snapshot"))
            {
                var denied = await plain.SendAsync(unauth);
                if (denied.StatusCode != HttpStatusCode.Unauthorized)
                {
                    log.Step("run-plus", false, $"expected 401 got {(int)denied.StatusCode}");
                    return 1;
                }
            }

            using (var auth = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{control}/v1/snapshot"))
            {
                auth.Headers.Add("X-Titanium-Control-Secret", secret);
                var snap = await plain.SendAsync(auth);
                if (snap.StatusCode != HttpStatusCode.OK)
                {
                    log.Step("run-plus", false, $"expected 200 snapshot got {(int)snap.StatusCode}");
                    return 1;
                }
            }

            // Proxied traffic through the same CLI process (user-like path).
            using var traffic = CreateDirectHttp();
            var proxied = await traffic.GetAsync($"http://127.0.0.1:{listen}/plus");
            var body = await proxied.Content.ReadAsStringAsync();
            var ok = proxied.StatusCode == HttpStatusCode.OK && body.Contains("echo:", StringComparison.Ordinal);
            log.Step("run-plus", ok, $"401/200 control + proxied status={(int)proxied.StatusCode}");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            log.Step("run-plus", false, ex.Message);
            return 1;
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static async Task<int> HelpStep(ProbeLog log, CliSpawn spawn, string id, string[] args, Func<string, bool> assert)
    {
        try
        {
            var (code, stdout, stderr) = await spawn.RunOnceAsync(args, timeout: TimeSpan.FromSeconds(20));
            var text = stdout + stderr;
            var ok = code == 0 && assert(text);
            log.Step(id, ok, $"exit={code}");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            log.Step(id, false, ex.Message);
            return 1;
        }
    }

    private static async Task<int> TestOk(ProbeLog log, CliSpawn spawn, string id, string cfg)
    {
        var (code, _, stderr) = await spawn.RunOnceAsync(["test", "-c", cfg]);
        var ok = code == 0;
        log.Step(id, ok, $"exit={code} {Trim(stderr)}");
        return ok ? 0 : 1;
    }

    private static bool ContainsAll(string text, params string[] needles) =>
        needles.All(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));

    private static string Trim(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= 160 ? s : s[..160] + "…";
    }

    private static string MakeTemp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cli-qa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // ignore
        }
    }

    private static async Task<string> ReadSharedAsync(string path)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }
}

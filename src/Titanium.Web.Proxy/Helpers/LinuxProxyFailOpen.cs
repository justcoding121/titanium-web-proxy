using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     After Inspector releases :port, a still-running Chrome with Preferences
///     <c>fixed_servers</c> keeps dialing that port. Spawn a tiny detached CONNECT/HTTP
///     tunnel so browsing does not break until Chrome is next restarted.
/// </summary>
internal static class LinuxProxyFailOpen
{
    private const string PidFileName = "fail-open-proxy.pid";
    private const string ScriptFileName = "fail-open-proxy.py";
    // Written alongside the pid file when the helper was started via systemd-run --scope.
    // Stop() reads this file to stop the scope unit before falling back to kill -TERM.
    private const string ScopeFileName = "fail-open-proxy.scope";
    private static int _ownedByThisProcess;

    /// <summary>True after <see cref="Start"/> in this process until an explicit <see cref="Stop"/>.</summary>
    internal static bool OwnedByThisProcess => Volatile.Read(ref _ownedByThisProcess) != 0;

    internal static void MarkOwnedByThisProcessForTests() => Volatile.Write(ref _ownedByThisProcess, 1);

    internal static void ClearOwnedByThisProcessForTests() => Volatile.Write(ref _ownedByThisProcess, 0);

    /// <summary>
    ///     Stop the helper only when it is the process listening for this Inspector port.
    ///     A real Inspector on that port is left alone.
    /// </summary>
    internal static void StopIfCoveringPort(int port)
    {
        if (port <= 0)
            return;
        try
        {
            var pidPath = PidPath();
            if (pidPath is null || !File.Exists(pidPath))
                return;
            if (!int.TryParse(File.ReadAllText(pidPath).Trim(), out var pid) || pid <= 1)
                return;
            var cmdPath = "/proc/" + pid + "/cmdline";
            if (!File.Exists(cmdPath))
                return;
            if (!CmdlineCoversPort(File.ReadAllText(cmdPath), port))
                return;
            Stop();
        }
        catch
        {
            // best-effort
        }
    }

    /// <summary>
    ///     True when a NUL-separated <c>/proc/.../cmdline</c> is the fail-open helper and one
    ///     argument is exactly <paramref name="port"/> (or <c>host:port</c>). Substring match
    ///     would treat port 80 as covering a helper on 8080.
    /// </summary>
    internal static bool CmdlineCoversPort(string cmdline, int port)
    {
        if (port <= 0 || string.IsNullOrEmpty(cmdline))
            return false;

        var parts = cmdline.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (!parts.Any(part => part.Contains("fail-open-proxy.py", StringComparison.Ordinal)))
            return false;

        var portToken = port.ToString();
        var portSuffix = ":" + portToken;
        return parts.Any(part =>
            string.Equals(part, portToken, StringComparison.Ordinal)
            || part.EndsWith(portSuffix, StringComparison.Ordinal));
    }

    /// <summary>
    ///     Stop a helper left by a dead Inspector. When this process just started the helper
    ///     for a graceful exit, leave it running so a browser pinned with <c>--proxy-server</c>
    ///     can still load. Returns false when the helper was kept.
    /// </summary>
    internal static bool StopUnlessOwnedByThisProcess()
    {
        if (Volatile.Read(ref _ownedByThisProcess) != 0)
            return false;
        Stop();
        return true;
    }

    internal static void Stop() // NOSONAR S3776 -- systemd scope stop, pidfile kill, and unit cleanup share fail-open teardown order
    {
        Volatile.Write(ref _ownedByThisProcess, 0);
        try
        {
            var dir = ConfigDir();
            if (dir is null)
                return;

            // If the helper was started in a systemd user scope, stop that scope first.
            // This handles the case where the helper was placed outside Inspector's cgroup
            // (so kill -TERM on the double-forked pid might not reach it after a scope stop).
            var scopePath = Path.Combine(dir, ScopeFileName);
            if (File.Exists(scopePath))
            {
                try
                {
                    var scopeName = File.ReadAllText(scopePath).Trim();
                    if (!string.IsNullOrEmpty(scopeName))
                        TryStopSystemdScope(scopeName);
                }
                catch
                {
                    // best-effort
                }
                finally
                {
                    try { File.Delete(scopePath); } catch { /* ignore */ }
                }
            }

            var pidPath = Path.Combine(dir, PidFileName);
            if (!File.Exists(pidPath))
                return;
            if (int.TryParse(File.ReadAllText(pidPath).Trim(), out var pid) && pid > 1)
            {
                var kill = UnixProcessPath.Resolve("/bin/kill", "/usr/bin/kill");
                if (kill is not null)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = kill,
                            Arguments = $"-TERM {pid}",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        })?.WaitForExit(2000);
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }

            try { File.Delete(pidPath); } catch { /* ignore */ }
        }
        catch
        {
            // ignore
        }
    }

    private static void TryStopSystemdScope(string scopeName)
    {
        var systemctl = UnixProcessPath.Resolve("/usr/bin/systemctl", "/bin/systemctl");
        if (systemctl is null)
            return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = systemctl,
                ArgumentList = { "--user", "stop", scopeName },
                UseShellExecute = false,
                CreateNoWindow = true,
            })?.WaitForExit(3000);
        }
        catch
        {
            // best-effort
        }
    }

    internal static void Start(string hostname, int port)
    {
        Stop();
        try
        {
            var dir = ConfigDir();
            if (dir is null)
                return;
            Directory.CreateDirectory(dir);
            var script = Path.Combine(dir, ScriptFileName);
            File.WriteAllText(script, FailOpenScript);
            var pidPath = Path.Combine(dir, PidFileName);
            var scopePath = Path.Combine(dir, ScopeFileName);
            var python = UnixProcessPath.Resolve("/usr/bin/python3", "/usr/local/bin/python3");
            if (python is null)
                return;

            // Prefer systemd-run --user --scope so the helper lives outside Inspector's
            // cgroup. When Inspector runs as a systemd user service, the service stops its
            // entire cgroup on exit — a setsid-only helper is killed with it. A scope unit
            // is independent and survives. Fall back to setsid when systemd-run is absent.
            var systemdRun = UnixProcessPath.Resolve("/usr/bin/systemd-run", "/bin/systemd-run");
            var xdgRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            var dbus = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
            var canUseSystemd = systemdRun is not null &&
                                !string.IsNullOrEmpty(xdgRuntime) &&
                                !string.IsNullOrEmpty(dbus);

            ProcessStartInfo psi;
            string? scopeName = null;

            if (canUseSystemd)
            {
                // Unit name must be unique per port so concurrent Inspector instances do not clash.
                scopeName = $"ti-fail-open-{port}.scope";
                psi = new ProcessStartInfo
                {
                    FileName = systemdRun!,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = dir,
                };
                psi.EnvironmentVariables["XDG_RUNTIME_DIR"] = xdgRuntime!;
                psi.EnvironmentVariables["DBUS_SESSION_BUS_ADDRESS"] = dbus!;
                psi.ArgumentList.Add("--user");
                psi.ArgumentList.Add("--scope");
                psi.ArgumentList.Add("--collect");
                psi.ArgumentList.Add($"--unit={scopeName}");
                psi.ArgumentList.Add(python);
                psi.ArgumentList.Add(script);
                psi.ArgumentList.Add(hostname);
                psi.ArgumentList.Add(port.ToString());
                psi.ArgumentList.Add(pidPath);
            }
            else
            {
                var setsid = UnixProcessPath.Resolve("/usr/bin/setsid", "/bin/setsid");
                if (setsid is null)
                    return;

                // setsid -f puts the helper in its own session so it is not killed when
                // the parent Inspector process exits (for menu-launch path).
                psi = new ProcessStartInfo
                {
                    FileName = setsid,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = dir,
                };
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add(python);
                psi.ArgumentList.Add(script);
                psi.ArgumentList.Add(hostname);
                psi.ArgumentList.Add(port.ToString());
                psi.ArgumentList.Add(pidPath);
            }

            if (!TryLaunchHelper(psi, scopeName, scopePath))
            {
                // systemd-run can fail even when the binary and session bus exist
                // (unit name clash, user manager not ready). Fall back to setsid.
                if (scopeName is null)
                    return;
                var setsid = UnixProcessPath.Resolve("/usr/bin/setsid", "/bin/setsid");
                if (setsid is null)
                    return;
                var fallback = new ProcessStartInfo
                {
                    FileName = setsid,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = dir,
                };
                fallback.ArgumentList.Add("-f");
                fallback.ArgumentList.Add(python);
                fallback.ArgumentList.Add(script);
                fallback.ArgumentList.Add(hostname);
                fallback.ArgumentList.Add(port.ToString());
                fallback.ArgumentList.Add(pidPath);
                TryLaunchHelper(fallback, scopeName: null, scopePath);
            }
        }
        catch
        {
            // best-effort — Preferences strip still runs for the next Chrome start
        }
    }

    /// <summary>
    ///     Starts <paramref name="psi"/> and treats a still-running process, or a zero exit,
    ///     as success. A quick non-zero exit means the launcher failed and the caller may
    ///     try the other start path. Ownership is taken before the wait so a same-process
    ///     clear cannot stop the helper mid-spawn, and cleared again when the launch fails.
    /// </summary>
    private static bool TryLaunchHelper(ProcessStartInfo psi, string? scopeName, string scopePath)
    {
        using var launched = Process.Start(psi);
        if (launched is null)
            return false;

        Volatile.Write(ref _ownedByThisProcess, 1);
        var exited = launched.WaitForExit(3000);
        if (exited && launched.ExitCode != 0)
        {
            Volatile.Write(ref _ownedByThisProcess, 0);
            return false;
        }

        if (scopeName is not null)
        {
            try { File.WriteAllText(scopePath, scopeName); }
            catch { /* best-effort */ }
        }

        return true;
    }

    private static string? ConfigDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            return null;
        return Path.Combine(home, ".config", "TitaniumInspector");
    }

    private static string? PidPath()
    {
        var dir = ConfigDir();
        return dir is null ? null : Path.Combine(dir, PidFileName);
    }

    // Minimal HTTP/HTTPS forwarder: CONNECT tunnels + absolute-form HTTP requests.
    private const string FailOpenScript =
        """
        #!/usr/bin/env python3
        import os, socket, select, sys, time

        host, port, pid_path = sys.argv[1], int(sys.argv[2]), sys.argv[3]

        if os.fork() > 0:
            sys.exit(0)
        os.setsid()
        if os.fork() > 0:
            sys.exit(0)
        with open(pid_path, "w") as f:
            f.write(str(os.getpid()))

        srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        # Inspector keeps the port until shutdown finishes. Twelve seconds was
        # not enough, so the helper exited and a pinned browser saw a closed port.
        deadline = time.time() + 90
        while True:
            try:
                srv.bind((host, port))
                break
            except OSError:
                if time.time() >= deadline:
                    sys.exit(1)
                time.sleep(0.15)
        srv.listen(128)

        def pipe(a, b):
            try:
                while True:
                    r, _, _ = select.select([a, b], [], [], 120)
                    if not r:
                        break
                    for s in r:
                        data = s.recv(65536)
                        if not data:
                            return
                        (b if s is a else a).sendall(data)
            except Exception:
                pass
            finally:
                try: a.close()
                except Exception: pass
                try: b.close()
                except Exception: pass

        def handle(c):
            try:
                c.settimeout(30)
                buf = b""
                while b"\r\n\r\n" not in buf and len(buf) < 65536:
                    chunk = c.recv(4096)
                    if not chunk:
                        c.close(); return
                    buf += chunk
                head, _, rest = buf.partition(b"\r\n\r\n")
                lines = head.split(b"\r\n")
                req = lines[0].decode("latin1", "replace")
                parts = req.split(" ")
                if len(parts) < 2:
                    c.close(); return
                method, target = parts[0].upper(), parts[1]
                if method == "CONNECT":
                    hostport = target
                    if ":" in hostport:
                        h, p = hostport.rsplit(":", 1)
                        p = int(p)
                    else:
                        h, p = hostport, 443
                    up = socket.create_connection((h, p), timeout=30)
                    c.sendall(b"HTTP/1.1 200 Connection Established\r\n\r\n")
                    if rest:
                        up.sendall(rest)
                    pipe(c, up)
                else:
                    # absolute-form: GET http://host/path HTTP/1.1
                    from urllib.parse import urlsplit
                    u = urlsplit(target)
                    h = u.hostname or ""
                    p = u.port or (443 if u.scheme == "https" else 80)
                    path = u.path or "/"
                    if u.query:
                        path += "?" + u.query
                    up = socket.create_connection((h, p), timeout=30)
                    new_req = f"{method} {path} {parts[2] if len(parts) > 2 else 'HTTP/1.1'}\r\n".encode()
                    # drop absolute Host confusion; keep remaining headers
                    hdrs = b"\r\n".join(lines[1:]) + b"\r\n\r\n"
                    up.sendall(new_req + hdrs + rest)
                    pipe(c, up)
            except Exception:
                try: c.close()
                except Exception: pass

        while True:
            try:
                c, _ = srv.accept()
            except Exception:
                break
            import threading
            threading.Thread(target=handle, args=(c,), daemon=True).start()
        """;
}
